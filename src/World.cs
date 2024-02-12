using System.Collections;
using Microsoft.Collections.Extensions;

namespace TinyEcs;

public sealed partial class World : IDisposable
{
    const ulong ECS_MAX_COMPONENT_FAST_ID = 256;
    const ulong ECS_START_USER_ENTITY_DEFINE = ECS_MAX_COMPONENT_FAST_ID;

    private readonly Archetype _archRoot;
    private readonly EntitySparseSet<EcsRecord> _entities = new();
    private readonly DictionarySlim<int, Archetype> _typeIndex = new();
    private Archetype[] _archetypes = new Archetype[16];
    private int _archetypeCount;
    private readonly ComponentComparer _comparer;
    private readonly Commands _commands;
    private int _frame;
    private EcsID _lastCompID = 1;

    public World()
    {
        _comparer = new ComponentComparer(this);
        _archRoot = new Archetype(
            this,
            ReadOnlySpan<EcsComponent>.Empty,
            _comparer
        );
        _commands = new(this);

        //InitializeDefaults();
        //_entities.MaxID = ECS_START_USER_ENTITY_DEFINE;
    }

    public int EntityCount => _entities.Length;

    public ReadOnlySpan<Archetype> Archetypes => _archetypes.AsSpan(0, _archetypeCount);

    public CommandEntityView DeferredEntity() => _commands.Entity();

    public void Merge() => _commands.Merge();

    public void Dispose()
    {
        _entities.Clear();
        _archRoot.Clear();
        _typeIndex.Clear();
        _commands.Clear();

        Array.Clear(_archetypes, 0, _archetypeCount);
        _archetypeCount = 0;
    }

    public void Optimize()
    {
        InternalOptimize(_archRoot);

        static void InternalOptimize(Archetype root)
        {
            root.Optimize();

            foreach (ref var edge in CollectionsMarshal.AsSpan(root._edgesRight))
            {
                InternalOptimize(edge.Archetype);
            }
        }
    }

    internal unsafe ref readonly EcsComponent Component<T>() where T : struct
	{
        ref readonly var lookup = ref Lookup.Component<T>.Value;

        // if (lookup.ID == 0 || !Exists(lookup.ID))
        // {
        //     EcsID id = lookup.ID;
        //     if (id == 0 && _lastCompID < ECS_MAX_COMPONENT_FAST_ID)
        //     {
        //         do
        //         {
        //             id = _lastCompID++;
        //         } while (Exists(id) && id <= ECS_MAX_COMPONENT_FAST_ID);
        //     }

        //     if (id >= ECS_MAX_COMPONENT_FAST_ID)
        //     {
        //         id = 0;
        //     }

        //     id = Entity(id);
        //     var size = GetSize<T>();

        //     lookup = new EcsComponent(id, size);
        //     _ = CreateComponent(id, size);
        // }

        // if (Exists(lookup.ID))
        // {
        //     var name = Lookup.Entity<T>.Name;
        //     ref var cmp2 = ref MemoryMarshal.GetReference(
        //         MemoryMarshal.Cast<byte, EcsComponent>(
        //             GetRaw(lookup.ID, EcsComponent, GetSize<EcsComponent>())
        //         )
        //     );

        //     EcsAssert.Panic(cmp2.Size == lookup.Size, $"invalid size for {Lookup.Entity<T>.Name}");
        // }

        return ref lookup;
    }

    //public Query Query() => new(this);

    public EntityView Entity(string name)
    {
        // TODO

        EcsID id = 0;

        return Entity(id);
    }

    public EntityView Entity(EcsID id = default)
    {
        return id == 0 || !Exists(id) ? NewEmpty(id) : new(this, id);
    }

    internal EntityView NewEmpty(ulong id = 0)
    {
        ref var record = ref (
            id > 0 ? ref _entities.Add(id, default!) : ref _entities.CreateNew(out id)
        );
        record.Archetype = _archRoot;
        record.Row = _archRoot.Add(id);
        record.Chunk = _archRoot.GetChunk(record.Row);

        return new(this, id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EcsRecord GetRecord(EcsID id)
    {
        ref var record = ref _entities.Get(id);
        EcsAssert.Assert(!Unsafe.IsNullRef(ref record));
        return ref record;
    }

    public void Delete(EcsID entity)
    {
        ref var record = ref GetRecord(entity);

        var removedId = record.Archetype.Remove(ref record);
        EcsAssert.Assert(removedId == entity);

        _entities.Remove(removedId);
    }

    public bool Exists(EcsID entity)
    {
        if (IDOp.IsPair(entity))
        {
            var first = IDOp.GetPairFirst(entity);
            var second = IDOp.GetPairSecond(entity);
            return _entities.Contains(first) && _entities.Contains(second);
        }

        return _entities.Contains(entity);
    }

    internal void DetachComponent(EcsID entity, ref readonly EcsComponent cmp)
    {
        ref var record = ref GetRecord(entity);
        InternalAttachDetach(ref record, in cmp, false);
    }

    private bool InternalAttachDetach(
        ref EcsRecord record,
        ref readonly EcsComponent cmp,
        bool add
    )
    {
        EcsAssert.Assert(!Unsafe.IsNullRef(ref record));

        var arch = CreateArchetype(record.Archetype, in cmp, add);
        if (arch == null)
            return false;

        record.Row = record.Archetype.MoveEntity(arch, record.Row);
        record.Archetype = arch!;
        record.Chunk = arch.GetChunk(record.Row);

        return true;
    }

    [SkipLocalsInit]
    private Archetype? CreateArchetype(Archetype root, ref readonly EcsComponent cmp, bool add)
    {
        if (!add && root.GetComponentIndex(in cmp) < 0)
            return null;

        var initType = root.Components;
        var cmpCount = Math.Max(0, initType.Length + (add ? 1 : -1));

        const int STACKALLOC_SIZE = 16;
		EcsComponent[]? buffer = null;
		scoped var span = cmpCount <= STACKALLOC_SIZE ? stackalloc EcsComponent[STACKALLOC_SIZE] : (buffer = ArrayPool<EcsComponent>.Shared.Rent(cmpCount));

		span = span[..cmpCount];

        if (!add)
        {
            for (int i = 0, j = 0; i < initType.Length; ++i)
            {
                if (initType[i].ID != cmp.ID)
                {
                    span[j++] = initType[i];
                }
            }
        }
        else if (!span.IsEmpty)
        {
            initType.CopyTo(span);
            span[^1] = cmp;
            span.Sort(_comparer);
        }

		ref var arch = ref GetArchetype(span, true);
		if (arch == null)
		{
			arch = _archRoot.InsertVertex(root, span, in cmp);

			if (_archetypeCount >= _archetypes.Length)
				Array.Resize(ref _archetypes, _archetypes.Length * 2);
			_archetypes[_archetypeCount++] = arch;
		}

		if (buffer != null)
            ArrayPool<EcsComponent>.Shared.Return(buffer);

        return arch;
    }

	internal ref Archetype? GetArchetype(Span<EcsComponent> components, bool create)
	{
		var hash = getHash(components, false);
		ref var arch = ref Unsafe.NullRef<Archetype>();
		if (create)
		{
			arch = ref _typeIndex.GetOrAddValueRef(hash, out var exists);
			if (!exists)
			{

			}
		}
		else if (_typeIndex.TryGetValue(hash, out arch))
		{

		}

		// ref var arch = ref create ? ref CollectionsMarshal.GetValueRefOrAddDefault(
		// 	_typeIndex,
		// 	hash,
		// 	out exists
		// ) : ref CollectionsMarshal.GetValueRefOrNullRef(_typeIndex, hash);

		return ref arch;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int getHash(Span<EcsComponent> components, bool checkSize)
		{
			var hc = components.Length;
			foreach (ref var val in components)
				hc = unchecked(hc * 314159 + val.ID);
			return hc;
		}
	}

	internal Array? Set(ref EcsRecord record, ref readonly EcsComponent cmp)
    {
        var emit = false;
        var column = record.Archetype.GetComponentIndex(in cmp);
        if (column < 0)
        {
            emit = InternalAttachDetach(ref record, in cmp, true);
            column = record.Archetype.GetComponentIndex(in cmp);
        }

        Array? array = null;

        if (cmp.Size > 0)
	        array = record.Chunk.RawComponentData(column);

        if (emit)
        {
            //EmitEvent(EcsEventOnSet, entity, cmp.ID);
        }

        return array;
    }

    internal bool Has(EcsID entity, ref readonly EcsComponent cmp)
    {
        ref var record = ref GetRecord(entity);
        return record.Archetype.GetComponentIndex(in cmp) >= 0;
    }

    public ReadOnlySpan<EcsComponent> GetType(EcsID id)
    {
        ref var record = ref GetRecord(id);
        return record.Archetype.Components;
    }

    public void PrintGraph()
    {
        _archRoot.Print();
    }
    //
    // [SkipLocalsInit]
    // public unsafe void RunPhase(ref readonly EcsComponent cmp)
    // {
    //     // Span<Term> terms = stackalloc Term[] { Term.With(Component<EcsSystem>().ID), Term.With(cmp.ID), };
    //     //
    //     // Query(terms, RunSystems);
    // }

    // public void Step(float deltaTime = 0.0f)
    // {
    //     _commands.Merge();
    //
    //     if (_frame == 0)
    //     {
    //         RunPhase(in Component<EcsSystemPhasePreStartup>());
    //         RunPhase(in Component<EcsSystemPhaseOnStartup>());
    //         RunPhase(in Component<EcsSystemPhasePostStartup>());
    //     }
    //
    //     RunPhase(in Component<EcsSystemPhasePreUpdate>());
    //     RunPhase(in Component<EcsSystemPhaseOnUpdate>());
    //     RunPhase(in Component<EcsSystemPhasePostUpdate>());
    //
    //     _commands.Merge();
    //     _frame += 1;
    //
    //     if (_frame % 10 == 0)
    //     {
    //         Optimize();
    //     }
    // }

    // static unsafe void RunSystems(ref Iterator it)
    // {
    //     var emptyIt = new Iterator(
    //         it.Commands,
    //         0,
    //         it.World._archRoot,
    //         Span<EntityView>.Empty,
    //         null,
    //         Span<Array>.Empty
    //     );
    //     var sysA = it.Field<EcsSystem>();
    //
    //     for (int i = 0; i < it.Count; ++i)
    //     {
    //         ref var sys = ref sysA[i];
    //
    //         if (!float.IsNaN(sys.Tick))
    //         {
    //             // TODO: check for it.DeltaTime > 0?
    //             sys.TickCurrent += it.DeltaTime;
    //
    //             if (sys.TickCurrent < sys.Tick)
    //             {
    //                 continue;
    //             }
    //
    //             sys.TickCurrent = 0;
    //         }
    //
    //         if (sys.Query.Value != 0)
    //         {
    //             it.World.Query(sys.Terms, sys.Callback);
    //         }
    //         else
    //         {
    //             sys.Callback(ref emptyIt);
    //         }
    //     }
    // }

    public FilterQuery<TFilter> Filter<TFilter>() where TFilter : struct
    {
	    return new FilterQuery<TFilter>(_archetypes.AsMemory(0, _archetypeCount));
    }
}

public readonly ref struct QueryInternal
{
	private readonly ReadOnlySpan<Archetype> _archetypes;
	private readonly ReadOnlySpan<Term> _terms;

	internal QueryInternal(ReadOnlySpan<Archetype> archetypes, ReadOnlySpan<Term> terms)
	{
		_archetypes = archetypes;
		_terms = terms;
	}

	public QueryIterator GetEnumerator()
	{
		return new QueryIterator(_archetypes, _terms);
	}
}

public ref struct QueryIterator
{
	private readonly ReadOnlySpan<Term> _terms;
	private readonly ReadOnlySpan<Archetype> _archetypes;
	private int _index;

	internal QueryIterator(ReadOnlySpan<Archetype> archetypes, ReadOnlySpan<Term> terms)
	{
		_archetypes = archetypes;
		_terms = terms;
		_index = -1;
	}

	public Archetype Current => _archetypes[_index];

	public bool MoveNext()
	{
		while (++_index < _archetypes.Length)
		{
			var arch = _archetypes[_index];
			var result = arch.FindMatch(_terms);
			if (result == 0 && arch.Count > 0)
				return true;
		}

		return false;
	}

	public void Reset() => _index = -1;
}

public partial interface IQuery
{
	QueryIterator GetEnumerator();
}

public partial interface IFilterOrQuery
{
	IQuery Filter<TFilter>() where TFilter : struct;
}

public readonly ref partial struct FilterQuery<TFilter> where TFilter : struct
{
	private readonly ReadOnlyMemory<Archetype> _archetypes;

	internal FilterQuery(ReadOnlyMemory<Archetype> archetypes)
	{
		_archetypes = archetypes;
	}

	public QueryIterator GetEnumerator()
    {
        return new QueryIterator(_archetypes.Span, Lookup.Query<TFilter>.Terms);
    }
}

internal static class Lookup
{
	private static int _index = -1;

	private static readonly Dictionary<int, Func<int, Array>> _arrayCreator = new Dictionary<int, Func<int, Array>>();
	private static readonly Dictionary<Type, int> _typesConvertion = new();

	public static Array? GetArray(int hashcode, int count)
	{
		var ok = _arrayCreator.TryGetValue(hashcode, out var fn);
		EcsAssert.Assert(ok, $"invalid hashcode {hashcode}");
		return fn?.Invoke(count) ?? null;
	}

	public static int GetID(Type type)
	{
		var ok = _typesConvertion.TryGetValue(type, out var id);
		EcsAssert.Assert(ok, $"invalid hashcode {type}");
		return id;
	}

	[SkipLocalsInit]
    internal static class Component<T> where T : struct
	{
        public static readonly int Size = GetSize();
        public static readonly string Name = typeof(T).ToString();
        public static readonly int HashCode = System.Threading.Interlocked.Increment(ref _index);

		public static readonly EcsComponent Value = new EcsComponent(HashCode, Size);

		static Component()
		{
			_arrayCreator.Add(Value.ID, count => Size > 0 ? new T[count] : Array.Empty<T>());
			_typesConvertion.Add(typeof(T), Value.ID);
			_typesConvertion.Add(typeof(With<T>), Value.ID);
			_typesConvertion.Add(typeof(Not<T>), -Value.ID);
			_typesConvertion.Add(typeof(Without<T>), -Value.ID);
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

    internal static class Query<TQuery, TFilter> where TQuery : struct where TFilter : struct
	{
		public static readonly Term[] Terms;
		public static readonly Term[] Columns;

		static Query()
		{
			var list = new List<Term>();
			if (typeof(ITuple).IsAssignableFrom(typeof(TQuery)))
			{
				ParseTuple((ITuple)default(TQuery), list);
			}
			else
			{
				list.Add(new Term() { ID = Lookup.GetID(typeof(TQuery)), Op = TermOp.With });
			}

			list.Sort();
			Columns = list.ToArray();

			if (typeof(ITuple).IsAssignableFrom(typeof(TFilter)))
			{
				ParseTuple((ITuple)default(TFilter), list);
			}
			else
			{
				var id = Lookup.GetID(typeof(TFilter));
				list.Add(new Term() { ID = Math.Abs(id), Op = id >= 0 ? TermOp.With : TermOp.Without });
			}

			list.Sort();
			Terms = list.ToArray();

			static void ParseTuple(ITuple tuple, List<Term> terms)
			{
				for (var i = 0; i < tuple.Length; ++i)
				{
					var type = tuple[i]!.GetType();

					if (typeof(ITuple).IsAssignableFrom(type))
					{
						ParseTuple((ITuple)tuple[i], terms);
						continue;
					}

					var id = Lookup.GetID(type);
					terms.Add(new Term()
					{
						ID = Math.Abs(id),
						Op = id >= 0 ? TermOp.With : TermOp.Without
					});
				}
			}
		}
	}

	internal static class Query<T> where T : struct
	{
		public static readonly Term[] Terms;

		static Query()
		{
			var list = new List<Term>();

			if (typeof(ITuple).IsAssignableFrom(typeof(T)))
			{
				ParseTuple((ITuple)default(T), list);
			}
			else
			{
				var id = Lookup.GetID(typeof(T));
				list.Add(new Term() { ID = Math.Abs(id), Op = id >= 0 ? TermOp.With : TermOp.Without });
			}

			list.Sort();
			Terms = list.ToArray();

			static void ParseTuple(ITuple tuple, List<Term> terms)
			{
				for (var i = 0; i < tuple.Length; ++i)
				{
					var type = tuple[i]!.GetType();

					if (typeof(ITuple).IsAssignableFrom(type))
					{
						ParseTuple((ITuple)tuple[i], terms);
						continue;
					}

					var id = Lookup.GetID(type);
					terms.Add(new Term()
					{
						ID = Math.Abs(id),
						Op = id >= 0 ? TermOp.With : TermOp.Without
					});
				}
			}
		}
	}
}

struct EcsRecord
{
	public Archetype Archetype;
    public ArchetypeChunk Chunk;
    public int Row;
}
