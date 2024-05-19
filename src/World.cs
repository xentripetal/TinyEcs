using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Collections.Extensions;

using static TinyEcs.Defaults;

namespace TinyEcs;

public sealed partial class World : IDisposable
{
    private readonly Archetype _archRoot;
    private readonly EntitySparseSet<EcsRecord> _entities = new();
    private readonly DictionarySlim<ulong, Archetype> _typeIndex = new();
    private Archetype[] _archetypes = new Archetype[16];
    private int _archetypeCount;
    private readonly ComponentComparer _comparer;
	private readonly EcsID _maxCmpId;
	private readonly Dictionary<ulong, Query> _cachedQueries = new ();
	private readonly object _newEntLock = new object();
	private readonly ConcurrentDictionary<string, EcsID> _namesToEntity = new ();


    public World(ulong maxComponentId = 256)
    {
        _comparer = new ComponentComparer(this);
        _archRoot = new Archetype(
            this,
            ImmutableArray<ComponentInfo>.Empty,
            _comparer
        );

		_maxCmpId = maxComponentId;
        _entities.MaxID = maxComponentId;

		_ = Component<DoNotDelete>();
		_ = Component<Unique>();
		_ = Component<Symmetric>();
		_ = Component<Wildcard>();
		_ = Component<(Wildcard, Wildcard)>();
		_ = Component<Identifier>();
		_ = Component<Name>();
		_ = Component<ChildOf>();

		setCommon(Entity<DoNotDelete>(), nameof(DoNotDelete));
		setCommon(Entity<Unique>(), nameof(Unique));
		setCommon(Entity<Symmetric>(), nameof(Symmetric));
		setCommon(Entity<Wildcard>(), nameof(Wildcard));
		setCommon(Entity<Identifier>(), nameof(Identifier));
		setCommon(Entity<Name>(), nameof(Name));
		setCommon(Entity<ChildOf>(), nameof(ChildOf))
			.Set<Unique>();

		static EntityView setCommon(EntityView entity, string name)
			=> entity.Set<DoNotDelete>().Set<Identifier, Name>(new (name));

		OnPluginInitialization?.Invoke(this);
    }

	public event Action<EntityView>? OnEntityCreated, OnEntityDeleted;
	public event Action<EntityView, ComponentInfo>? OnComponentSet, OnComponentUnset;
	public static event Action<World>? OnPluginInitialization;

    public int EntityCount => _entities.Length;
	internal Archetype Root => _archRoot;

    public ReadOnlySpan<Archetype> Archetypes => _archetypes.AsSpan(0, _archetypeCount);


    public void Dispose()
    {
        _entities.Clear();
        _archRoot.Clear();
        _typeIndex.Clear();

		foreach (var query in _cachedQueries.Values)
			query.Dispose();

		_cachedQueries.Clear();
		_namesToEntity.Clear();

        Array.Clear(_archetypes, 0, _archetypeCount);
        _archetypeCount = 0;
    }

    internal ref readonly ComponentInfo Component<T>() where T : struct
	{
        ref readonly var lookup = ref Lookup.Component<T>.Value;

		EcsAssert.Panic(lookup.ID.IsPair || lookup.ID < _maxCmpId,
			"Increase the minimum number for components when initializing the world [ex: new World(1024)]");

		if (!lookup.ID.IsPair && !Exists(lookup.ID))
		{
			var e = Entity(lookup.ID)
				.Set(lookup);
		}

        return ref lookup;
    }

	public EntityView Entity<T>() where T : struct
	{
		ref readonly var cmp = ref Component<T>();

		var entity = Entity(cmp.ID);

		var name = Lookup.Component<T>.Name;

		if (_namesToEntity.TryGetValue(name, out var id))
		{
			EcsAssert.Panic(entity.ID == id, $"You must declare the component before the entity '{id}' named '{name}'");
		}
		else
		{
			_namesToEntity[name] = entity;
			entity.Set<Identifier, Name>(new (name));
		}

		return entity;
	}

    public EntityView Entity(EcsID id = default)
    {
        return id == 0 || !Exists(id) ? NewEmpty(id) : new(this, id);
    }

	public EntityView Entity(string name)
	{
		if (string.IsNullOrEmpty(name))
			return EntityView.Invalid;

		EntityView entity;
		if (_namesToEntity.TryGetValue(name, out var id))
		{
			entity = Entity(id);
		}
		else
		{
			entity = Entity();
			_namesToEntity[name] = entity;
			entity.Set<Identifier, Name>(new (name));
		}

		return entity;
	}

    internal EntityView NewEmpty(ulong id = 0)
    {
		lock (_newEntLock)
		{
			// if (IsDeferred)
			// {
			// 	if (id == 0)
			// 		id = ++_entities.MaxID;
			// 	CreateDeferred(id);
			// 	return new EntityView(this, id);
			// }

			ref var record = ref (
				id > 0 ? ref _entities.Add(id, default!) : ref _entities.CreateNew(out id)
			);
			record.Archetype = _archRoot;
			record.Row = _archRoot.Add(id);

			var e = new EntityView(this, id);

			OnEntityCreated?.Invoke(e);

			return e;
		}
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EcsRecord GetRecord(EcsID id)
    {
        ref var record = ref _entities.Get(id);
        EcsAssert.Assert(!Unsafe.IsNullRef(ref record), $"entity {id} is dead or doesn't exist!");
        return ref record;
    }

    public void Delete(EcsID entity)
    {
		if (IsDeferred)
		{
			if (Exists(entity))
				DeleteDeferred(entity);

			return;
		}

		lock (_newEntLock)
		{
			OnEntityDeleted?.Invoke(new (this, entity));

			EcsAssert.Panic(!Has<DoNotDelete>(entity), "You can't delete this entity!");

			if (Has<Identifier, Name>(entity))
			{
				var name = Get<Identifier, Name>(entity).Value;
				_namesToEntity.Remove(name, out var _);
			}

			// TODO: remove the allocations
			// TODO: check for this interesting flecs approach:
			// 		 https://github.com/SanderMertens/flecs/blob/master/include/flecs/private/api_defines.h#L289
			var term0 = new Term(IDOp.Pair(Wildcard.ID, entity), TermOp.With);
			var term1 = new Term(IDOp.Pair(entity, Wildcard.ID), TermOp.With);
			QueryRaw([term0]).Each((EntityView child) => child.Delete());
			QueryRaw([term1]).Each((EntityView child) => child.Delete());


			ref var record = ref GetRecord(entity);

			var removedId = record.Archetype.Remove(ref record);
			EcsAssert.Assert(removedId == entity);

			_entities.Remove(removedId);
		}
    }

    public bool Exists(EcsID entity)
    {
		// if (IsDeferred && ExistsDeferred(entity))
		// 	return true;

		if (entity.IsPair)
        {
            return _entities.Contains(entity.First) && _entities.Contains(entity.Second);
        }

        return _entities.Contains(entity);
    }

	private void DetachComponent(EcsID entity, EcsID id)
	{
		ref var record = ref GetRecord(entity);
		var oldArch = record.Archetype;

		if (oldArch.GetComponentIndex(id) < 0)
            return;

		var cmp = Lookup.GetComponent(id, -1);
		OnComponentUnset?.Invoke(record.GetChunk().EntityAt(record.Row), cmp);

		var newSign = oldArch.Components.Remove(cmp, _comparer);
		EcsAssert.Assert(newSign.Length < oldArch.Components.Length, "bad");

		ref var newArch = ref GetArchetype(newSign, true);
		if (newArch == null)
		{
			newArch = _archRoot.InsertVertex(oldArch, newSign, cmp.ID);

			if (_archetypeCount >= _archetypes.Length)
				Array.Resize(ref _archetypes, _archetypes.Length * 2);
			_archetypes[_archetypeCount++] = newArch;
		}

		record.Row = record.Archetype.MoveEntity(newArch!, record.Row);
        record.Archetype = newArch!;
	}

	private (Array?, int) AttachComponent(EcsID entity, EcsID id, int size)
	{
		ref var record = ref GetRecord(entity);
		var oldArch = record.Archetype;

		var index = oldArch.GetComponentIndex(id);
		if (index >= 0)
            return (size > 0 ? record.GetChunk().RawComponentData(index) : null, record.Row);

		var cmp = Lookup.GetComponent(id, size);
		var newSign = oldArch.Components.Add(cmp).Sort(_comparer);
		EcsAssert.Assert(newSign.Length > oldArch.Components.Length, "bad");

		ref var newArch = ref GetArchetype(newSign, true);
		if (newArch == null)
		{
			newArch = _archRoot.InsertVertex(oldArch, newSign, cmp.ID);

			if (_archetypeCount >= _archetypes.Length)
				Array.Resize(ref _archetypes, _archetypes.Length * 2);
			_archetypes[_archetypeCount++] = newArch;
		}

		record.Row = record.Archetype.MoveEntity(newArch, record.Row);
        record.Archetype = newArch!;

		OnComponentSet?.Invoke(record.GetChunk().EntityAt(record.Row), cmp);

		return (size > 0 ? record.GetChunk().RawComponentData(newArch.GetComponentIndex(cmp.ID)) : null, record.Row);
	}

    private ref Archetype? GetArchetype(ImmutableArray<ComponentInfo> components, bool create)
	{
		var hash = Hashing.Calculate(components.AsSpan());
		ref var arch = ref Unsafe.NullRef<Archetype>();
		if (create)
		{
			arch = ref _typeIndex.GetOrAddValueRef(hash, out var exists)!;
			if (!exists)
			{

			}
		}
		else if (_typeIndex.TryGetValue(hash, out arch))
		{

		}

		return ref arch;
	}

    internal bool Has(EcsID entity, EcsID id)
    {
		// if (IsDeferred)
		// {
		// 	if (HasDeferred(entity, id))
		// 		return true;

		// 	if (ExistsDeferred(entity))
		// 		return false;
		// }

		ref var record = ref GetRecord(entity);
        var has = record.Archetype.GetComponentIndex(id) >= 0;
		if (has) return true;

		if (id.IsPair)
		{
			(var a, var b) = FindPair(entity, id);

			return a != 0 && b != 0;
		}

		return id == Wildcard.ID;
    }

    public ReadOnlySpan<ComponentInfo> GetType(EcsID id)
    {
        ref var record = ref GetRecord(id);
        return record.Archetype.Components.AsSpan();
    }

    public void PrintGraph()
    {
        _archRoot.Print();
    }

	public Query QueryRaw(ImmutableArray<Term> terms)
	{
		return GetQuery(
			Hashing.Calculate(terms.AsSpan()),
			terms,
			static (world, terms) => new Query(world, terms));
	}

	public Query Query<TQuery>() where TQuery : struct
	{
		return GetQuery(
			Lookup.Query<TQuery>.Hash,
		 	Lookup.Query<TQuery>.Terms,
		 	static (world, _) => new Query<TQuery>(world)
		);
	}

	public Query Query<TQuery, TFilter>() where TQuery : struct where TFilter : struct
	{
		return GetQuery(
			Lookup.Query<TQuery, TFilter>.Hash,
			Lookup.Query<TQuery, TFilter>.Terms,
		 	static (world, _) => new Query<TQuery, TFilter>(world)
		);
	}

	public void Each(QueryFilterDelegateWithEntity fn)
	{
		BeginDeferred();

		foreach (var arch in GetQuery(0, ImmutableArray<Term>.Empty, static (world, terms) => new Query(world, terms)))
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

		EndDeferred();
	}

	internal Query GetQuery(ulong hash, ImmutableArray<Term> terms, Func<World, ImmutableArray<Term>, Query> factory)
	{
		if (!_cachedQueries.TryGetValue(hash, out var query))
		{
			query = factory(this, terms);
			_cachedQueries.Add(hash, query);
		}

		query.Match();

		return query;
	}

    public QueryBuilder QueryBuilder() => new QueryBuilder(this);

	internal Archetype? FindArchetype(ulong hash)
	{
		if (!_typeIndex.TryGetValue(hash, out var arch))
		{
			arch = _archRoot;
		}

		return arch;
	}

	internal void MatchArchetypes(Archetype root, ReadOnlySpan<Term> terms, List<Archetype> matched)
	{
		var result = root.FindMatch(terms);
		if (result < 0)
		{
			return;
		}

		if (result == 0)
		{
			matched.Add(root);
		}

		var span = CollectionsMarshal.AsSpan(root._edgesRight);
		if (span.IsEmpty)
			return;

		ref var start = ref MemoryMarshal.GetReference(span);
		ref var end = ref Unsafe.Add(ref start, span.Length);

		while (Unsafe.IsAddressLessThan(ref start, ref end))
		{
			MatchArchetypes(start.Archetype, terms, matched);
			start = ref Unsafe.Add(ref start, 1);
		}
	}
}

internal static class Hashing
{
	const ulong FIXED = 314159;

	public static ulong Calculate(ReadOnlySpan<ComponentInfo> components)
	{
		var hc = (ulong)components.Length;
		foreach (ref readonly var val in components)
			hc = unchecked(hc * FIXED + val.ID);
		return hc;
	}

	public static ulong Calculate(ReadOnlySpan<Term> terms)
	{
		var hc = (ulong)terms.Length;
		foreach (ref readonly var val in terms)
			hc = unchecked(hc * FIXED + (ulong)val.IDs.Sum(s => s.ID) + (byte)val.Op);
		return hc;
	}

	public static ulong Calculate(IEnumerable<EcsID> terms)
	{
		var hc = (ulong)terms.Count();
		foreach (var val in terms)
			hc = unchecked(hc * FIXED + val);
		return hc;
	}
}

internal static class Lookup
{
	private static ulong _index = 0;

	private static readonly Dictionary<ulong, Func<int, Array>> _arrayCreator = new ();
	private static readonly Dictionary<Type, Term> _typesConvertion = new();
	private static readonly Dictionary<Type, ComponentInfo> _componentInfosByType = new();
	private static readonly Dictionary<EcsID, ComponentInfo> _components = new ();

	public static Array? GetArray(ulong hashcode, int count)
	{
		var ok = _arrayCreator.TryGetValue(hashcode, out var fn);
		EcsAssert.Assert(ok, $"component not found with hashcode {hashcode}");
		return fn?.Invoke(count) ?? null;
	}

	public static ComponentInfo GetComponent(EcsID id, int size)
	{
		if (!_components.TryGetValue(id, out var cmp))
		{
			cmp = new ComponentInfo(id, size);
			// TODO: i don't want to store non generics stuff
			//_components.Add(id, cmp);
		}

		return cmp;
	}

	private static Term GetTerm(Type type)
	{
		var ok = _typesConvertion.TryGetValue(type, out var term);
		EcsAssert.Assert(ok, $"component not found with type {type}");
		return term;
	}

	[SkipLocalsInit]
    internal static class Component<T> where T : struct
	{
        public static readonly int Size = GetSize();
        public static readonly string Name = GetName();
        public static readonly ulong HashCode;
		public static readonly ComponentInfo Value;

		static Component()
		{
			if (typeof(ITuple).IsAssignableFrom(typeof(T)))
			{
				var tuple = (ITuple)default(T);
				EcsAssert.Panic(tuple.Length == 2, "Relations must be composed by 2 arguments only.");

				var firstId = GetTerm(tuple[0]!.GetType());
				var secondId = GetTerm(tuple[1]!.GetType());
				var pairId = IDOp.Pair(firstId.IDs[0], secondId.IDs[0]);

				HashCode = pairId;
				Size = 0;

				if (_componentInfosByType.TryGetValue(tuple[1]!.GetType(), out var secondCmpInfo))
				{
					Size = secondCmpInfo.Size;
				}
			}
			else
			{
				HashCode = (ulong)System.Threading.Interlocked.Increment(ref Unsafe.As<ulong, int>(ref _index));
			}

			Value = new ComponentInfo(HashCode, Size);
			_arrayCreator.Add(Value.ID, count => Size > 0 ? new T[count] : Array.Empty<T>());

			_typesConvertion.Add(typeof(T), new (Value.ID, TermOp.With));
			_typesConvertion.Add(typeof(With<T>), new (Value.ID, TermOp.With));
			_typesConvertion.Add(typeof(Not<T>), new (Value.ID, TermOp.Without));
			_typesConvertion.Add(typeof(Without<T>), new (Value.ID, TermOp.Without));
			_typesConvertion.Add(typeof(Optional<T>), new (Value.ID, TermOp.Optional));

			_componentInfosByType.Add(typeof(T), Value);

			_components.Add(Value.ID, Value);
		}

		private static string GetName()
		{
			var name = typeof(T).ToString();

			var indexOf = name.LastIndexOf('.');
			if (indexOf >= 0)
				name = name[(indexOf + 1) ..];

			return name;
		}

		private static int GetSize()
		{
			var size = RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? IntPtr.Size : Unsafe.SizeOf<T>();

			if (size != 1)
				return size;

			// credit: BeanCheeseBurrito from Flecs.NET
			Unsafe.SkipInit<T>(out var t1);
			Unsafe.SkipInit<T>(out var t2);
			Unsafe.As<T, byte>(ref t1) = 0x7F;
			Unsafe.As<T, byte>(ref t2) = 0xFF;

			return ValueType.Equals(t1, t2) ? 0 : size;
		}
    }

	static void ParseTuple(ITuple tuple, List<Term> terms)
	{
		var mainType = tuple.GetType();
		TermOp? op = null;
		var tmpTerms = terms;

		if (typeof(IAtLeast).IsAssignableFrom(mainType))
		{
			op = TermOp.AtLeastOne;
			tmpTerms = new ();
		}
		else if (typeof(IExactly).IsAssignableFrom(mainType))
		{
			op = TermOp.Exactly;
			tmpTerms = new ();
		}
		else if (typeof(INone).IsAssignableFrom(mainType))
		{
			op = TermOp.None;
			tmpTerms = new ();
		}
		else if (typeof(IOr).IsAssignableFrom(mainType))
		{
			op = TermOp.Or;
			tmpTerms = new ();
		}

		for (var i = 0; i < tuple.Length; ++i)
		{
			var type = tuple[i]!.GetType();

			if (typeof(ITuple).IsAssignableFrom(type))
			{
				ParseTuple((ITuple)tuple[i]!, terms);
				continue;
			}

			var term = GetTerm(type);
			tmpTerms.Add(term);
		}

		if (op.HasValue)
		{
			terms.Add(new Term(tmpTerms.SelectMany(s => s.IDs), op.Value));
		}
	}

	static void ParseType<T>(List<Term> terms) where T : struct
	{
		var type = typeof(T);
		if (_typesConvertion.TryGetValue(type, out var term))
		{
			terms.Add(term);

			return;
		}

		if (typeof(ITuple).IsAssignableFrom(type))
		{
			ParseTuple((ITuple)default(T), terms);

			return;
		}

		EcsAssert.Panic(false, $"Type {type} is not registered. Register {type} using world.Entity<T>() or assign it to an entity.");
	}

    internal static class Query<TQuery, TFilter>
		where TQuery : struct
		where TFilter : struct
	{
		public static readonly ImmutableArray<Term> Terms;
		public static readonly ulong Hash;

		static Query()
		{
			var list = new List<Term>();

			ParseType<TQuery>(list);
			ParseType<TFilter>(list);

			Terms = list.ToImmutableArray();

			list.Sort();
			Hash = Hashing.Calculate(list.ToArray());
		}
	}

	internal static class Query<TQuery> where TQuery : struct
	{
		public static readonly ImmutableArray<Term> Terms;
		public static readonly ulong Hash;

		static Query()
		{
			var list = new List<Term>();

			ParseType<TQuery>(list);

			Terms = list.ToImmutableArray();

			list.Sort();
			Hash = Hashing.Calculate(list.ToArray());
		}
	}
}

struct EcsRecord
{
	public Archetype Archetype;
    public int Row;

    public readonly ref ArchetypeChunk GetChunk() => ref Archetype.GetChunk(Row);
}
