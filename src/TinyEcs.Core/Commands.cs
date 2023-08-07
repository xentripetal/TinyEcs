using System.Numerics;

namespace TinyEcs;

public sealed class Commands
{
	private readonly World _main;
	private readonly EntitySparseSet<EntityID> _despawn;
	private readonly EntitySparseSet<SetComponent> _set;
	private readonly EntitySparseSet<UnsetComponent> _unset;

	public Commands(World main)
	{
		_main = main;
		_despawn = new();
		_set = new();
		_unset = new();
	}


	public CommandEntityView Entity(EntityID id)
	{
		EcsAssert.Assert(_main.IsAlive(id));

		return new CommandEntityView(this, id);
	}

	public CommandEntityView Spawn()
	{
		var ent = _main.SpawnEmpty();

		return new CommandEntityView(this, ent.ID)
			.Set<EcsEnabled>();
	}

	public void Despawn(EntityID id)
	{
		EcsAssert.Assert(_main.IsAlive(id));

		ref var entity = ref _despawn.Get(id);
		if (Unsafe.IsNullRef(ref entity))
		{
			_despawn.Add(id, id);
		}
	}

	public unsafe void Set<T>(EntityID id) where T : unmanaged
	{
		EcsAssert.Assert(_main.IsAlive(id));

		ref var cmp = ref _main.Component<T>(true);
		if (_main.Has(id, ref cmp))
		{
			return;
		}

		ref var set = ref _set.CreateNew(out _);
		set.Entity = id;
		set.Component = cmp;

		if (set.Data.Length < set.Component.Size)
		{
			set.Data = new byte[Math.Max(sizeof(ulong), (int) BitOperations.RoundUpToPowerOf2((uint) set.Component.Size))];
		}
	}

	public unsafe ref T Set<T>(EntityID id, T component) where T : unmanaged
	{
		EcsAssert.Assert(_main.IsAlive(id));

		ref var cmp = ref _main.Component<T>();
		if (_main.Has(id, ref cmp))
		{
			ref var value = ref _main.Get<T>(id);
			value = component;

			return ref value;
		}

		ref var set = ref _set.CreateNew(out _);
		set.Entity = id;
		set.Component = cmp;

		if (set.Data.Length < set.Component.Size)
		{
			set.Data = new byte[Math.Max(sizeof(ulong), (int) BitOperations.RoundUpToPowerOf2((uint) set.Component.Size))];
		}

		ref var reference = ref MemoryMarshal.GetReference(set.Data.Span.Slice(0, set.Component.Size));

		if (!Unsafe.IsNullRef(ref reference))
		{
			fixed (byte* ptr = &reference)
				Unsafe.Copy(ptr, ref component);
		}

		return ref Unsafe.As<byte, T>(ref reference);
	}

	public unsafe void Add(EntityID id, ref EcsComponent cmp)
	{
		EcsAssert.Assert(_main.IsAlive(id));

		if (_main.Has(id, ref cmp))
		{
			return;
		}

		ref var set = ref _set.CreateNew(out _);
		set.Entity = id;
		set.Component = cmp;

		if (set.Data.Length < set.Component.Size)
		{
			set.Data = new byte[Math.Max(sizeof(ulong), (int) BitOperations.RoundUpToPowerOf2((uint) set.Component.Size))];
		}
	}

	public unsafe void Add(EntityID id, EntityID first, EntityID second)
	{
		var cmp = new EcsComponent(IDOp.Pair(first, second), 0);
		Add(id, ref cmp);
	}

	public unsafe void Add<TKind, TTarget>(EntityID id) where TKind : unmanaged where TTarget : unmanaged
	{
		Add(id, _main.Component<TKind>().ID,  _main.Component<TTarget>().ID);
	}

	public void Unset<T>(EntityID id) where T : unmanaged
	{
		EcsAssert.Assert(_main.IsAlive(id));

		ref var cmp = ref _main.Component<T>();

		ref var unset = ref _unset.CreateNew(out _);
		unset.Entity = id;
		unset.Component = cmp;
	}

	public ref T Get<T>(EntityID entity) where T : unmanaged
	{
		EcsAssert.Assert(_main.IsAlive(entity));

		if (_main.Has<T>(entity))
		{
			return ref _main.Get<T>(entity);
		}

		Unsafe.SkipInit<T>(out var cmp);

		return ref Set(entity, cmp);
	}

	public bool Has<T>(EntityID entity) where T : unmanaged
	{
		EcsAssert.Assert(_main.IsAlive(entity));

		return _main.Has<T>(entity);
	}


	public void Merge()
	{
		if (_despawn.Length == 0 && _set.Length == 0 && _unset.Length == 0)
		{
			return;
		}

		foreach (ref var set in _set)
		{
			EcsAssert.Assert(_main.IsAlive(set.Entity));

			_main.Set(set.Entity, ref set.Component, set.Component.Size <= 0 ? ReadOnlySpan<byte>.Empty : set.Data.Span.Slice(0, set.Component.Size));
		}

		foreach (ref var unset in _unset)
		{
			EcsAssert.Assert(_main.IsAlive(unset.Entity));

			_main.DetachComponent(unset.Entity, ref unset.Component);
		}

		foreach (ref var despawn in _despawn)
		{
			_main.Despawn(despawn);
		}


		_set.Clear();
		_unset.Clear();
		_despawn.Clear();
	}

	private struct SetComponent
	{
		public EntityID Entity;
		public EcsComponent Component;
		public Memory<byte> Data;
	}

	private struct UnsetComponent
	{
		public EntityID Entity;
		public EcsComponent Component;
	}
}

public readonly ref struct CommandEntityView
{
	private readonly EntityID _id;
	private readonly Commands _cmds;

	internal CommandEntityView(Commands cmds, EntityID id)
	{
		_cmds = cmds;
		_id = id;
	}

	public readonly EntityID ID => _id;

	public readonly CommandEntityView Set<T>() where T : unmanaged
	{
		_cmds.Set<T>(_id);
		return this;
	}

	public readonly CommandEntityView Set<T>(T cmp) where T : unmanaged
	{
		_cmds.Set(_id, cmp);
		return this;
	}

	public readonly CommandEntityView Add(EntityID id)
	{
		_cmds.Add(_id, id, 0);
		return this;
	}

	public readonly CommandEntityView Add(EntityID first, EntityID second)
	{
		_cmds.Add(_id, first, second);
		return this;
	}

	public readonly CommandEntityView Add<TKind, TTarget>()
		where TKind : unmanaged
		where TTarget : unmanaged
	{
		_cmds.Add<TKind, TTarget>(_id);
		return this;
	}

	public readonly CommandEntityView Unset<T>() where T : unmanaged
	{
		_cmds.Unset<T>(_id);
		return this;
	}

	public readonly CommandEntityView Despawn()
	{
		_cmds.Despawn(_id);
		return this;
	}

	public readonly ref T Get<T>() where T : unmanaged
	{
		return ref _cmds.Get<T>(_id);
	}

	public readonly bool Has<T>() where T : unmanaged
	{
		return _cmds.Has<T>(_id);
	}
}
