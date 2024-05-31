using System.Collections.Immutable;

namespace TinyEcs;

public ref struct QueryInternal
{
#if NET
	private ref Archetype _value;
	private readonly ref Archetype _first, _last;
#else
	private Ref<Archetype> _value;
	private readonly Ref<Archetype> _first, _last;
#endif


	internal QueryInternal(Span<Archetype> archetypes)
	{
#if NET
		_first = ref MemoryMarshal.GetReference(archetypes);
		_last = ref Unsafe.Add(ref _first, archetypes.Length);
		_value = ref Unsafe.NullRef<Archetype>();
#else
		_first = new(ref MemoryMarshal.GetReference(archetypes));
		_last = new(ref Unsafe.Add(ref _first.Value, archetypes.Length));
		_value = new(ref Unsafe.NullRef<Archetype>());
#endif
	}

	public readonly ref Archetype Current => ref
		_value
#if !NET
		.Value
#endif
		;


	public bool MoveNext()
	{
		while (true)
		{
#if NET
			_value = ref Unsafe.IsNullRef(ref _value) ? ref _first : ref Unsafe.Add(ref _value, 1);
			if (!Unsafe.IsAddressLessThan(ref _value, ref _last))
				break;

			if (_value.Count > 0)
				return true;
#else
			ref var value = ref _value.Value;
			value = ref Unsafe.IsNullRef(ref value) ? ref _first.Value : ref Unsafe.Add(ref value, 1);
			if (!Unsafe.IsAddressLessThan(ref value, ref _last.Value))
				break;

			if (value.Count > 0)
				return true;
#endif
		}

		return false;
	}

	public readonly QueryInternal GetEnumerator() => this;
}

public delegate void QueryFilterDelegateWithEntity(EntityView entity);



public sealed class QueryBuilder
{
	private readonly World _world;
	private readonly SortedSet<QueryTerm> _components = new();

	internal QueryBuilder(World world) => _world = world;

	public QueryBuilder With<T>() where T : struct
		=> With(_world.Component<T>().ID);

	public QueryBuilder With<TAction, TTarget>()
		where TAction : struct
		where TTarget : struct
		=> With(_world.Component<TAction>().ID, _world.Component<TTarget>().ID);

	public QueryBuilder With<TAction>(EcsID target)
		where TAction : struct
		=> With(_world.Component<TAction>().ID, target);

	public QueryBuilder With(EcsID action, EcsID target)
		=> With(IDOp.Pair(action, target));

	public QueryBuilder With(EcsID id)
	{
		_components.Add(new(id, TermOp.With));
		return this;
	}

	public QueryBuilder Without<T>() where T : struct
		=> Without(_world.Component<T>().ID);

	public QueryBuilder Without<TAction, TTarget>()
		where TAction : struct
		where TTarget : struct
		=> Without(_world.Component<TAction>().ID, _world.Component<TTarget>().ID);

	public QueryBuilder Without<TAction>(EcsID target)
		where TAction : struct
		=> Without(_world.Component<TAction>().ID, target);

	public QueryBuilder Without(EcsID action, EcsID target)
		=> Without(IDOp.Pair(action, target));

	public QueryBuilder Without(EcsID id)
	{
		_components.Add(new (id, TermOp.Without));
		return this;
	}

	public QueryBuilder Optional<T>() where T :struct
		=> Optional(_world.Component<T>().ID);

	public QueryBuilder Optional(EcsID id)
	{
		_components.Add(new (id, TermOp.Optional));
		return this;
	}

	public Query Build()
	{
		var terms = _components.ToImmutableArray();
		return _world.GetQuery(
			Hashing.Calculate(terms.AsSpan()),
			terms,
			static (world, terms) => new Query(world, terms)
		);
	}
}


public sealed partial class Query<TQueryData> : Query
	where TQueryData : struct
{
	internal Query(World world) : base(world, Lookup.Query<TQueryData>.Terms)
	{
	}
}

public sealed partial class Query<TQueryData, TQueryFilter> : Query
	where TQueryData : struct where TQueryFilter : struct
{
	internal Query(World world) : base(world, Lookup.Query<TQueryData, TQueryFilter>.Terms)
	{
	}
}

public partial class Query : IDisposable
{
	private readonly ImmutableArray<QueryTerm> _terms;
	private readonly List<Archetype> _matchedArchetypes;
	private ulong _lastArchetypeIdMatched = 0;
	private Query? _subQuery;

	internal Query(World world, ImmutableArray<QueryTerm> terms)
	{
		World = world;
		_matchedArchetypes = new List<Archetype>();

		_terms = terms.Where(s => s.Op != TermOp.Or)
			.ToImmutableSortedSet()
			.ToImmutableArray();

		ref var subQuery = ref _subQuery;
		foreach (var or in terms.OfType<ContainerQueryTerm>()
			.Where(s => s.Op == TermOp.Or))
		{
			subQuery = World.GetQuery
			(
				Hashing.Calculate(or.Terms),
				[.. or.Terms],
				static (world, terms) => new Query(world, terms)
			);

			subQuery = ref subQuery._subQuery;
		}
	}

	public World World { get; internal set; }

	internal CountdownEvent ThreadCounter { get; } = new CountdownEvent(1);



	public void Dispose()
	{
		_subQuery?.Dispose();
		ThreadCounter.Dispose();
	}

	internal void Match()
	{
		_subQuery?.Match();

		var allArchetypes = World.Archetypes;

		if (allArchetypes.IsEmpty || _lastArchetypeIdMatched == allArchetypes[^1].Id)
			return;

		var ids = _terms
			.Where(s => s.Op == TermOp.With || s.Op == TermOp.Exactly)
			.Select(s => s.Id);

		var first = World.FindArchetype(Hashing.Calculate(ids));
		if (first == null)
			return;

		_lastArchetypeIdMatched = allArchetypes[^1].Id;
		_matchedArchetypes.Clear();
		World.MatchArchetypes(first, _terms.AsSpan(), _matchedArchetypes);
	}

	public int Count()
	{
		Match();

		var count = _matchedArchetypes.Sum(static s => s.Count);
		if (count == 0 && _subQuery != null)
		{
			return _subQuery.Count();
		}

		return count;
	}

	public ref T Single<T>() where T : struct
	{
		var count = Count();
		EcsAssert.Panic(count == 1, "Multiple entities found for a single archetype");

		foreach (var arch in this)
		{
			var column = arch.GetComponentIndex<T>();
			EcsAssert.Panic(column > 0, "component not found");
			ref var value = ref arch.GetChunk(0).GetReference<T>(column);
			return ref value;
		}

		return ref Unsafe.NullRef<T>();
	}

	public EntityView Single()
	{
		var count = Count();
		EcsAssert.Panic(count == 1, "Multiple entities found for a single archetype");

		foreach (var arch in this)
		{
			return arch.GetChunk(0).EntityAt(0);
		}

		return EntityView.Invalid;
	}

	public QueryInternal GetEnumerator()
	{
		Match();

		if (_subQuery != null)
		{
			if (_matchedArchetypes.All(static s => s.Count == 0))
			{
				return _subQuery.GetEnumerator();
			}
		}

		return new (CollectionsMarshal.AsSpan(_matchedArchetypes));
	}

	public void Each(QueryFilterDelegateWithEntity fn)
	{
		World.BeginDeferred();

		foreach (var arch in this)
		{
			foreach (ref readonly var chunk in arch)
			{
				ref var entity = ref chunk.EntityAt(0);
				ref var last = ref Unsafe.Add(ref entity, chunk.Count);
				while (Unsafe.IsAddressLessThan(ref entity, ref last))
				{
					fn(entity);
					entity = ref Unsafe.Add(ref entity, 1);
				}
			}
		}

		World.EndDeferred();
	}
}
