namespace TinyEcs;

public struct ArchetypeChunk
{
	public Array[]? Components;
	public EntityView[] Entities;
	public int Count;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ref T GetReference<T>(int column) where T : struct
	{
		EcsAssert.Assert(column >= 0 && column < Components!.Length);
		ref var array = ref Unsafe.As<Array, T[]>(ref Components![column]);
		return ref MemoryMarshal.GetArrayDataReference(array);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<T> GetSpan<T>(int column) where T : struct
	{
		EcsAssert.Assert(column >= 0 && column < Components!.Length);
		ref var array = ref Unsafe.As<Array, T[]>(ref Components![column]);
		return array.AsSpan(0, Count);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal Array RawComponentData(int column)
	{
		EcsAssert.Assert(column >= 0 && column < Components!.Length);
		return Components![column];
	}
}

public sealed class Archetype
{
    const int ARCHETYPE_INITIAL_CAPACITY = 16;

    private int _lastChunkIndex;
    private ArchetypeChunk[] _chunks;
    private const int CHUNK_THRESHOLD = 4096;

    private readonly World _world;
    private readonly ComponentComparer _comparer;
    private readonly int[] _lookup;
    private int _count;
    internal List<EcsEdge> _edgesLeft, _edgesRight;

	internal Archetype(
        World world,
        ReadOnlySpan<EcsComponent> components,
        ComponentComparer comparer
    )
    {
        _comparer = comparer;
        _world = world;
        _edgesLeft = new List<EcsEdge>();
        _edgesRight = new List<EcsEdge>();
        Components = components.ToArray();

        var maxID = -1;
        for (var i = 0; i < components.Length; ++i)
	        maxID = Math.Max(maxID, components[i].ID);

        _lookup = new int[maxID + 1];
        _lookup.AsSpan().Fill(-1);
        for (var i = 0; i < components.Length; ++i)
	        _lookup[components[i].ID] = i;

        _chunks = new ArchetypeChunk[ARCHETYPE_INITIAL_CAPACITY];
    }

    public World World => _world;
    public int Count => _count;
    public readonly EcsComponent[] Components;
    public Span<ArchetypeChunk> Chunks => _chunks.AsSpan(0, (_count / CHUNK_THRESHOLD) + 1);

    [SkipLocalsInit]
    public ref ArchetypeChunk GetChunk(int index)
    {
	    if (index >= _chunks.Length)
		    Array.Resize(ref _chunks, _chunks.Length * 2);

	    ref var chunk = ref _chunks[index / CHUNK_THRESHOLD];
	    if (chunk.Components == null)
	    {
		    chunk.Entities = new EntityView[CHUNK_THRESHOLD];
		    chunk.Components = new Array[Components.Length];
		    for (var i = 0; i < Components.Length; ++i)
			    chunk.Components[i] = Lookup.GetArray(Components[i].ID, CHUNK_THRESHOLD)!;
	    }

	    return ref chunk;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndex(ref readonly EcsComponent cmp)
    {
	    return GetComponentIndex(cmp.ID);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal int GetComponentIndex(int id)
	{
		return id >= 0 && id < _lookup.Length ? _lookup[id] : -1;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int GetComponentIndex<T>() where T : struct
	{
		var id = Lookup.Entity<T>.HashCode;
		return id >= 0 && id < _lookup.Length ? _lookup[id] : -1;
	}

	internal int Add(EcsID id)
	{
		ref var chunk = ref GetChunk(_count);
		chunk.Entities[_count % CHUNK_THRESHOLD] = new(_world, id);
		chunk.Count = _count % CHUNK_THRESHOLD;
        return _count++;
    }

    internal EcsID Remove(ref EcsRecord record)
    {
		var removed = SwapWithLast(record.Row);
		ref var chunk = ref GetChunk(record.Row);

		for (int i = 0; i < Components.Length; ++i)
		{
			var leftArray = chunk.RawComponentData(i);

			var tmp = leftArray.GetValue(_count - 1);
			leftArray.SetValue(tmp, record.Row);
		}

		chunk.Count--;
		--_count;

        return removed;
    }

	internal Archetype InsertVertex(
        Archetype left,
        ReadOnlySpan<EcsComponent> components,
        ref readonly EcsComponent component
    )
    {
        var vertex = new Archetype(left._world, components, _comparer);
        MakeEdges(left, vertex, component.ID);
        InsertVertex(vertex);
        return vertex;
    }

    internal int MoveEntity(Archetype to, int fromRow)
    {
		var removed = SwapWithLast(fromRow);
		var toRow = to.Add(removed);
        MoveTo(fromRow, to, toRow);

        --_count;

        return toRow;
    }

    private void MoveTo(int fromRow, Archetype to, int toRow)
	{
		var isLeft = to.Components.Length < Components.Length;
		int i = 0,
			j = 0;
		var count = isLeft ? to.Components.Length : Components.Length;

		ref var x = ref (isLeft ? ref j : ref i);
		ref var y = ref (!isLeft ? ref j : ref i);

		var last = _count - 1;

		ref var fromChunk = ref GetChunk(fromRow);
		ref var toChunk = ref to.GetChunk(toRow);

		for (; x < count; ++x, ++y)
		{
			while (Components[i].ID != to.Components[j].ID)
			{
				// advance the sign with less components!
				++y;
			}

			var fromArray = fromChunk.RawComponentData(i);
			var toArray = toChunk.RawComponentData(j);

			// copy the moved entity to the target archetype
			Array.Copy(fromArray, fromRow % CHUNK_THRESHOLD, toArray, toRow % CHUNK_THRESHOLD, 1);

			// swap last with the hole
			Array.Copy(fromArray, last % CHUNK_THRESHOLD, fromArray, fromRow % CHUNK_THRESHOLD, 1);
		}

		fromChunk.Count--;
		toChunk.Count++;
		//_count = fromCount;
	}

	internal void Clear()
    {
        _count = 0;
    }

    internal void Optimize()
    {

    }

    private EcsID SwapWithLast(int fromRow)
    {
	    ref var chunkFrom = ref GetChunk(fromRow);
	    ref var chunkTo = ref GetChunk(_count - 1);

        ref var fromRec = ref _world.GetRecord(chunkFrom.Entities[fromRow % CHUNK_THRESHOLD]);
        ref var lastRec = ref _world.GetRecord(chunkTo.Entities[(_count - 1) % CHUNK_THRESHOLD]);
        lastRec.Row = fromRec.Row;

        var removed = chunkFrom.Entities[fromRow % CHUNK_THRESHOLD];
        chunkFrom.Entities[fromRow % CHUNK_THRESHOLD] = chunkTo.Entities[(_count - 1) % CHUNK_THRESHOLD];

        return removed;
    }

    private static void MakeEdges(Archetype left, Archetype right, int id)
    {
        left._edgesRight.Add(new EcsEdge() { Archetype = right, ComponentID = id });
        right._edgesLeft.Add(new EcsEdge() { Archetype = left, ComponentID = id });
    }

    private void InsertVertex(Archetype newNode)
    {
        var nodeTypeLen = Components.Length;
        var newTypeLen = newNode.Components.Length;

        if (nodeTypeLen > newTypeLen - 1)
        {
            return;
        }

        if (nodeTypeLen < newTypeLen - 1)
        {
#if NET5_0_OR_GREATER
            foreach (ref var edge in CollectionsMarshal.AsSpan(_edgesRight))
#else
            foreach (var edge in _edgesRight)
#endif
            {
                edge.Archetype.InsertVertex(newNode);
            }

            return;
        }

        if (!IsSuperset(newNode.Components))
        {
            return;
        }

        var i = 0;
        var newNodeTypeLen = newNode.Components.Length;
        for (; i < newNodeTypeLen && Components[i].ID == newNode.Components[i].ID; ++i) { }

        MakeEdges(newNode, this, Components[i].ID);
    }

    internal bool IsSuperset(Span<EcsComponent> other)
    {
	    int i = 0, j = 0;
	    while (i < Components.Length && j < other.Length)
	    {
		    if (Components[i].ID == other[j].ID)
		    {
			    j++;
		    }

		    i++;
	    }

	    return j == other.Length;
    }

    internal int FindMatch(Span<Term> searching)
    {
	    var currents = Components;
	    var i = 0;
	    var j = 0;

        while (i < currents.Length && j < searching.Length)
        {
	        ref var current = ref currents[i];
	        ref var search = ref searching[j];

            if (current.ID.CompareTo(search.ID) == 0)
            {
                if (search.Op != TermOp.With)
                    return -1;

                ++j;
            }
            else if (current.ID > search.ID && search.Op != TermOp.With)
            {
	            ++j;
                continue;
            }

            ++i;
        }

        while (j < searching.Length && searching[j].Op != TermOp.With)
	        ++j;

        return j == searching.Length ? 0 : 1;
    }

    public void Print()
    {
        PrintRec(this, 0, 0);

        static void PrintRec(Archetype root, int depth, int rootComponent)
        {
            Console.WriteLine(
                "{0}Parent [{1}] common ID: {2}",
                new string('\t', depth),
                string.Join(", ", root.Components.Select(s => s.ID)),
                rootComponent
            );

            if (root._edgesRight.Count > 0)
                Console.WriteLine("{0}Children: ", new string('\t', depth));

            //Console.WriteLine("{0}[{1}] |{2}| - Table [{3}]", new string('.', depth), string.Join(", ", root.ComponentInfo.Select(s => s.ID)), rootComponent, string.Join(", ", root.Table.Components.Select(s => s.ID)));

            foreach (ref readonly var edge in CollectionsMarshal.AsSpan(root._edgesRight))
            {
                PrintRec(edge.Archetype, depth + 1, edge.ComponentID);
            }
        }
    }
}

struct EcsEdge
{
    public int ComponentID;
    public Archetype Archetype;
}
