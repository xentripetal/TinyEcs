using System.Drawing;

namespace TinyEcs;


public interface IComponentStub { }
public interface IComponent : IComponentStub { }
public interface ITag : IComponentStub { }
public interface IEvent : ITag { }


public readonly struct EcsComponent : IComponent
{
	public readonly ulong ID;
	public readonly int Size;

	public EcsComponent(EcsID id, int size)
	{
		ID = id;
		Size = size;
	}
}

public unsafe struct EcsSystem<TContext> : IComponent
{
	const int TERMS_COUNT = 32;

	public readonly delegate*<ref Iterator<TContext>, void> Callback;
	public readonly EcsID Query;
	public readonly float Tick;
	public float TickCurrent;
	private fixed byte _terms[TERMS_COUNT * (sizeof(ulong) + sizeof(byte))];
	private readonly int _termsCount;

	public Span<Term> Terms
	{
		get
		{
			fixed (byte* ptr = _terms)
			{
				return new Span<Term>(ptr, _termsCount);
			}
		}
	}

	public EcsSystem(delegate*<ref Iterator<TContext>, void> func, EcsID query, ReadOnlySpan<Term> terms, float tick)
	{
		Callback = func;
		Query = query;
		_termsCount = terms.Length;
		terms.CopyTo(Terms);
		Terms.Sort();
		Tick = tick;
		TickCurrent = 0f;
	}
}


public unsafe struct EcsEvent<TContext> : IComponent
{
	const int TERMS_COUNT = 16;

	public readonly delegate*<ref Iterator<TContext>, void> Callback;

	private fixed byte _terms[TERMS_COUNT * (sizeof(ulong) + sizeof(TermOp))];
	private readonly int _termsCount;

	public Span<Term> Terms
	{
		get
		{
			fixed (byte* ptr = _terms)
			{
				return new Span<Term>(ptr, _termsCount);
			}
		}
	}

	public EcsEvent(delegate*<ref Iterator<TContext>, void> callback, ReadOnlySpan<Term> terms)
	{
		Callback = callback;
		_termsCount = terms.Length;
		var currentTerms = Terms;
		terms.CopyTo(currentTerms);
		currentTerms.Sort();
	}
}


public struct EcsEventOnSet : IEvent { }
public struct EcsEventOnUnset : IEvent { }
public struct EcsPhase : ITag { }
public struct EcsPanic : ITag { }
public struct EcsDelete : ITag { }
public struct EcsExclusive : ITag { }
public struct EcsAny : ITag { }
public struct EcsTag : ITag { }
public struct EcsChildOf : ITag { }
public struct EcsDisabled : ITag { }
public struct EcsSystemPhaseOnUpdate : ITag { }
public struct EcsSystemPhasePreUpdate : ITag { }
public struct EcsSystemPhasePostUpdate : ITag { }
public struct EcsSystemPhaseOnStartup : ITag { }
public struct EcsSystemPhasePreStartup : ITag { }
public struct EcsSystemPhasePostStartup : ITag { }



public partial class World<TContext>
{
	public const ulong EcsComponent = 1;

	public const ulong EcsPanic = 2;
	public const ulong EcsDelete = 3;
	public const ulong EcsTag = 4;

	public const ulong EcsExclusive = 5;
	public const ulong EcsChildOf = 6;
	public const ulong EcsPhase = 7;

	public const ulong EcsEventOnSet = 8;
	public const ulong EcsEventOnUnset = 9;

	public const ulong EcsPhaseOnPreStartup = 9;
	public const ulong EcsPhaseOnStartup = 10;
	public const ulong EcsPhaseOnPostStartup = 11;
	public const ulong EcsPhaseOnPreUpdate = 12;
	public const ulong EcsPhaseOnUpdate = 13;
	public const ulong EcsPhaseOnPostUpdate = 14;



	internal unsafe void InitializeDefaults()
	{
		var ecsComponent = CreateWithLookup<EcsComponent>(EcsComponent);

		var ecsPanic = CreateWithLookup<EcsPanic>(EcsPanic);
		var ecsDelete = CreateWithLookup<EcsDelete>(EcsDelete);
		var ecsTag = CreateWithLookup<EcsTag>(EcsTag);

		var ecsExclusive = CreateWithLookup<EcsExclusive>(EcsExclusive);
		var ecsChildOf = CreateWithLookup<EcsChildOf>(EcsChildOf);
		var ecsPhase = CreateWithLookup<EcsPhase>(EcsPhase);

		var ecsEventOnSet = CreateWithLookup<EcsEventOnSet>(EcsEventOnSet);
		var ecsEventOnUnset = CreateWithLookup<EcsEventOnUnset>(EcsEventOnUnset);

		var ecsPreStartup = CreateWithLookup<EcsSystemPhasePreStartup>(EcsPhaseOnPreStartup);
		var ecsStartup = CreateWithLookup<EcsSystemPhaseOnStartup>(EcsPhaseOnStartup);
		var ecsPostStartup = CreateWithLookup<EcsSystemPhasePostStartup>(EcsPhaseOnPostStartup);
		var ecsPreUpdate = CreateWithLookup<EcsSystemPhasePreUpdate>(EcsPhaseOnPreUpdate);
		var ecsUpdate = CreateWithLookup<EcsSystemPhaseOnUpdate>(EcsPhaseOnUpdate);
		var ecsPostUpdate = CreateWithLookup<EcsSystemPhasePostUpdate>(EcsPhaseOnPostUpdate);


		ecsChildOf.Set(EcsExclusive);
		ecsPhase.Set(ecsExclusive); // NOTE: do we want to make phase singletons?


		var cmp2 = Lookup<TContext>.Entity<EcsComponent>.Component = new (ecsComponent, GetSize<EcsComponent>());
		Set(ecsComponent.ID, ref cmp2, new ReadOnlySpan<byte>(Unsafe.AsPointer(ref cmp2), cmp2.Size));
		ecsComponent.Set(EcsPanic, EcsDelete);


		SetBaseTags<EcsPanic>(ecsExclusive);
		SetBaseTags<EcsDelete>(ecsChildOf);
		SetBaseTags<EcsTag>(ecsPhase);

		SetBaseTags<EcsExclusive>(ecsPanic);
		SetBaseTags<EcsChildOf>(ecsDelete);
		SetBaseTags<EcsPhase>(ecsTag);

		SetBaseTags<EcsEventOnSet>(ecsEventOnSet);
		SetBaseTags<EcsEventOnUnset>(ecsEventOnUnset);

		SetBaseTags<EcsSystemPhasePreStartup>(ecsPreStartup);
		SetBaseTags<EcsSystemPhaseOnStartup>(ecsStartup);
		SetBaseTags<EcsSystemPhasePostStartup>(ecsPostStartup);
		SetBaseTags<EcsSystemPhasePreUpdate>(ecsPreUpdate);
		SetBaseTags<EcsSystemPhaseOnUpdate>(ecsUpdate);
		SetBaseTags<EcsSystemPhasePostUpdate>(ecsPostUpdate);


		EntityView<TContext> CreateWithLookup<T>(EcsID id) where T : unmanaged, IComponentStub
		{
			var view = Entity(id);
			Lookup<TContext>.Entity<T>.Component = new (view, GetSize<T>());
			return view;
		}

		EntityView<TContext> SetBaseTags<T>(EntityView<TContext> view) where T : unmanaged, IComponentStub
			=> view.Set(Lookup<TContext>.Entity<T>.Component).Set(EcsPanic, EcsDelete).Set(EcsTag);
	}
}
