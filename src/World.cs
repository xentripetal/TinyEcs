using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Collections.Extensions;

namespace TinyEcs;

public sealed partial class World : IDisposable
{
	internal delegate Query QueryFactoryDel(World world, ReadOnlySpan<IQueryTerm> terms);

    private readonly Archetype _archRoot;
    private readonly EntitySparseSet<EcsRecord> _entities = new();
    private readonly FastIdLookup<Archetype> _typeIndex = new();
    private readonly ComponentComparer _comparer;
	private readonly EcsID _maxCmpId;
	private readonly Dictionary<EcsID, Query> _cachedQueries = new ();
	private readonly object _newEntLock = new object();
	private readonly ConcurrentDictionary<string, EcsID> _namesToEntity = new ();
	private readonly FastIdLookup<EcsID> _cacheIndex = new ();


	internal Archetype Root => _archRoot;
    internal List<Archetype> Archetypes { get; } = [];



	public void Each(QueryFilterDelegateWithEntity fn)
	{
		BeginDeferred();

		foreach (var arch in GetQuery(0, [], static (world, terms) => new Query(world, terms)))
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

	internal ref readonly ComponentInfo Component<T>() where T : struct
	{
        ref readonly var lookup = ref Lookup.Component<T>.Value;

		var isPair = lookup.ID.IsPair();
		EcsAssert.Panic(isPair || lookup.ID < _maxCmpId,
			"Increase the minimum number for components when initializing the world [ex: new World(1024)]");

		if (!isPair /*!Exists(lookup.ID)*/)
		{
			ref var idx = ref _cacheIndex.GetOrCreate(lookup.ID, out var exists);

			if (!exists)
			{
				idx = Entity(lookup.ID).Set(lookup).ID;
			}
		}

		return ref lookup;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EcsRecord GetRecord(EcsID id)
    {
        ref var record = ref _entities.Get(id);
		if (Unsafe.IsNullRef(ref record))
        	EcsAssert.Panic(false, $"entity {id} is dead or doesn't exist!");
        return ref record;
    }

	public EcsID GetAlive(EcsID id)
	{
		if (Exists(id))
			return id;

		if ((uint)id != id)
			return 0;

		var current = _entities.GetNoGeneration(id);
		if (current == 0)
			return 0;

		if (!Exists(current))
			return 0;

		return current;
	}

	private void DetachComponent(EcsID entity, EcsID id)
	{
		ref var record = ref GetRecord(entity);
		var oldArch = record.Archetype;

		if (oldArch.GetComponentIndex(id) < 0)
            return;

		if (id.IsPair())
		{
			(var first, var second) = id.Pair();
			GetRecord(GetAlive(first)).Flags &= ~EntityFlags.IsAction;
			GetRecord(GetAlive(second)).Flags &= ~EntityFlags.IsTarget;
		}

		OnComponentUnset?.Invoke(record.GetChunk().EntityAt(record.Row), new ComponentInfo(id, -1));

		BeginDeferred();
		var roll = oldArch.Rolling;
		roll.Remove(id);

		Archetype? found = null;
		ref var arch = ref _typeIndex.TryGet(roll.Hash, out var exists);

		if (exists)
		{
			found = arch;
		}

		if (!exists)
		{
			var count = oldArch.All.Length - 1;
			if (count <= 0)
			{
				found = _archRoot;
			}
			else
			{
				var tmp = ArrayPool<ComponentInfo>.Shared.Rent(oldArch.All.Length - 1);
				var newSign = tmp.AsSpan(0, oldArch.All.Length - 1);

				for (int i = 0, j = 0; i < oldArch.All.Length; ++i)
				{
					if (oldArch.All[i].ID != id)
						newSign[j++] = oldArch.All[i];
				}

				ref var newArch = ref GetArchetype(newSign, true);
				if (newArch == null)
				{
					newArch = _archRoot.InsertVertex(oldArch, newSign, id);
					Archetypes.Add(newArch);
				}

				ArrayPool<ComponentInfo>.Shared.Return(tmp);

				found = newArch;
			}
		}

		record.Row = record.Archetype.MoveEntity(found!, record.Row);
        record.Archetype = found!;
		EndDeferred();
	}

	static readonly Comparison<ComponentInfo> _comparison = (a, b) => ComponentComparer.CompareTerms(null!, a.ID, b.ID);

	private (Array?, int) AttachComponent(EcsID entity, EcsID id, int size)
	{
		ref var record = ref GetRecord(entity);
		var oldArch = record.Archetype;

		var index = oldArch.GetComponentIndex(id);
		if (index >= 0)
            return (size > 0 ? record.GetChunk().RawComponentData(index) : null, record.Row);

		if (id.IsPair())
		{
			(var first, var second) = id.Pair();
			GetRecord(GetAlive(first)).Flags |= EntityFlags.IsAction;
			GetRecord(GetAlive(second)).Flags |= EntityFlags.IsTarget;
		}

		BeginDeferred();
		var roll = oldArch.Rolling;
		roll.Add(id);

		Archetype? found = null;

		ref var archFound = ref _typeIndex.TryGet(roll.Hash, out var exists);

		if (exists)
		{
			found = archFound;
		}

		if (!exists)
		{
			var tmp = ArrayPool<ComponentInfo>.Shared.Rent(oldArch.All.Length + 1);
			var newSign = tmp.AsSpan(0, oldArch.All.Length + 1);
			oldArch.All.CopyTo(newSign);
			newSign[^1] = new ComponentInfo(id, size);
#if NET
			MemoryExtensions.Sort(newSign, _comparison);
#else
			newSign.Sort(_comparer);
#endif

			ref var newArch = ref GetArchetype(newSign, true);
			if (newArch == null)
			{
				newArch = _archRoot.InsertVertex(oldArch, newSign, id);
				Archetypes.Add(newArch);
			}

			ArrayPool<ComponentInfo>.Shared.Return(tmp);
			found = newArch;
		}

		record.Row = record.Archetype.MoveEntity(found!, record.Row);
        record.Archetype = found!;
		EndDeferred();

		OnComponentSet?.Invoke(record.GetChunk().EntityAt(record.Row), new ComponentInfo(id, size));

		return (size > 0 ? record.GetChunk().RawComponentData(found!.GetComponentIndex(id)) : null, record.Row);
	}

    private ref Archetype? GetArchetype(ReadOnlySpan<ComponentInfo> components, bool create)
	{
		var rolling = new RollingHash();
		foreach (ref readonly var cmp in components)
			rolling.Add(cmp.ID);

		ref var arch = ref Unsafe.NullRef<Archetype>();

		if (create)
		{
			arch = ref _typeIndex.GetOrCreate(rolling.Hash, out _)!;
		}
		else
		{
			arch = ref _typeIndex.TryGet(rolling.Hash, out _)!;
		}

		return ref arch;
	}

	internal ref T GetUntrusted<T>(EcsID entity, EcsID cmpId, int size) where T : struct
	{
		if (IsDeferred && !Has(entity, cmpId))
		{
			Unsafe.SkipInit<T>(out var val);
			return ref Unsafe.Unbox<T>(SetDeferred(entity, cmpId, val, size)!);
		}

        ref var record = ref GetRecord(entity);
		var column = record.Archetype.GetComponentIndex(cmpId);
        ref var chunk = ref record.GetChunk();
		ref var value = ref Unsafe.Add(ref chunk.GetReference<T>(column), record.Row & Archetype.CHUNK_THRESHOLD);
		return ref value;
    }

	internal Query GetQuery(EcsID hash, ReadOnlySpan<IQueryTerm> terms, QueryFactoryDel factory)
	{
		if (!_cachedQueries.TryGetValue(hash, out var query))
		{
			query = factory(this, terms);
			_cachedQueries.Add(hash, query);
		}

		query.Match();

		return query;
	}

	internal Archetype? FindArchetype(EcsID hash)
	{
		ref var arch = ref _typeIndex.TryGet(hash, out var exists);
		if (!exists)
			return _archRoot;
		return arch;
	}

	internal void MatchArchetypes(Archetype root, ReadOnlySpan<IQueryTerm> terms, List<Archetype> matched)
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

		var add = root._add;
		if (add.Count <= 0)
			return;

		foreach ((var id, var edge) in add)
		{
			MatchArchetypes(edge.Archetype, terms, matched);
		}
	}
}

struct EcsRecord
{
	public Archetype Archetype;
    public int Row;
	public EntityFlags Flags;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref ArchetypeChunk GetChunk() => ref Archetype.GetChunk(Row);
}

[Flags]
enum EntityFlags
{
	None = 1 << 0,
	IsAction = 1 << 1,
	IsTarget = 1 << 2,
	IsUnique = 1 << 3
}
