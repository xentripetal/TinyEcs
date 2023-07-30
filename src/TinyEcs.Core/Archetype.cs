using System.Numerics;

namespace TinyEcs;

public sealed unsafe class Archetype
{
	const int ARCHETYPE_INITIAL_CAPACITY = 16;

	private readonly World _world;
	private int _capacity, _count;
	private EntityID[] _entityIDs;
	internal byte[][] _componentsData;
	internal List<EcsEdge> _edgesLeft, _edgesRight;
	//private readonly EntitySparseSet<EntityID> _lookup;
	private readonly EntityID[] _components;
	private readonly Dictionary<EntityID, int> _lookup = new();

	internal Archetype(World world, ReadOnlySpan<EcsComponent> components)
	{
		_world = world;
		_capacity = ARCHETYPE_INITIAL_CAPACITY;
		_count = 0;
		_components = new EntityID[components.Length];
		ComponentInfo = components.ToArray();
		_entityIDs = new EntityID[ARCHETYPE_INITIAL_CAPACITY];
		_componentsData = new byte[components.Length][];
		_edgesLeft = new List<EcsEdge>();
		_edgesRight = new List<EcsEdge>();
		//_lookup = new EntitySparseSet<EntityID>();

		for (var i = 0; i < components.Length; i++)
		{
			_components[i] = components[i].ID;
			//_lookup.Add(components[i].ID, (EntityID)i);
			_lookup.Add(components[i].ID, i);
		}

		ResizeComponentArray(ARCHETYPE_INITIAL_CAPACITY);
	}


	internal readonly EcsComponent[] ComponentInfo;


	internal EntityID[] Entities => _entityIDs;
	internal EntityID[] Components => _components;

	public World World => _world;
	public int Count => _count;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal int GetComponentIndex(EntityID component)
	{
		ref var idx = ref CollectionsMarshal.GetValueRefOrNullRef(_lookup, component);
		//ref var idx = ref _lookup.Get(component);

		return Unsafe.IsNullRef(ref idx) ? -1 : (int)idx;
	}

	internal int Add(EntityID entityID)
	{
		if (_capacity == _count)
		{
			_capacity *= 2;

			Array.Resize(ref _entityIDs, _capacity);
			ResizeComponentArray(_capacity);
		}

		_entityIDs[_count] = entityID;

		return _count++;
	}

	internal EntityID Remove(int row)
	{
		var removed = _entityIDs[row];
		_entityIDs[row] = _entityIDs[_count - 1];

		for (int i = 0; i < _components.Length; ++i)
		{
			ref readonly var meta = ref ComponentInfo[i];
			var leftArray = _componentsData[i].AsSpan();

			var removeComponent = leftArray.Slice(meta.Size * row, meta.Size);
			var swapComponent = leftArray.Slice(meta.Size * (_count - 1), meta.Size);

			swapComponent.CopyTo(removeComponent);
		}

		--_count;

		return removed;
	}

	internal Archetype InsertVertex(Archetype left, ReadOnlySpan<EcsComponent> newType, EntityID component)
	{
		var vertex = new Archetype(left._world, newType);
		MakeEdges(left, vertex, component);
		InsertVertex(vertex);
		return vertex;
	}

	internal static int MoveEntity(Archetype from, Archetype to, int fromRow)
	{
		var removed = from._entityIDs[fromRow];
		from._entityIDs[fromRow] = from._entityIDs[from._count - 1];

		var toRow = to.Add(removed);

		Copy(from, fromRow, to, toRow);

		--from._count;

		return toRow;
	}

	internal Span<byte> GetComponentRaw(EntityID component, int row, int count)
	{
		var column = GetComponentIndex(component);
		if (column < 0)
		{
			return Span<byte>.Empty;
		}

		//EcsAssert.Assert(row < Count); // this is not true when removing

		ref readonly var meta = ref ComponentInfo[column];

		return _componentsData[column].AsSpan(meta.Size * row, meta.Size * count);
	}

	internal void Clear()
	{
		_count = 0;
		_capacity = ARCHETYPE_INITIAL_CAPACITY;
		Array.Resize(ref _entityIDs, _capacity);
		ResizeComponentArray(_capacity);
	}

	internal void Optimize()
	{
		var pow = (int) BitOperations.RoundUpToPowerOf2((uint) _count);
		var newCapacity = Math.Max(ARCHETYPE_INITIAL_CAPACITY, pow);
		if (newCapacity < _capacity)
		{
			_capacity = newCapacity;
			Array.Resize(ref _entityIDs, _capacity);
			ResizeComponentArray(_capacity);
		}
	}

	static void Copy(Archetype from, int fromRow, Archetype to, int toRow)
	{
		var isLeft = to._components.Length < from._components.Length;
		int i = 0, j = 0;
		var count = isLeft ? to._components.Length : from._components.Length;

		ref var x = ref (isLeft ? ref j : ref i);
		ref var y = ref (!isLeft ? ref j : ref i);

		for (; x < count; ++x, ++y)
		{
			while (from._components[i] != to._components[j])
			{
				// advance the sign with less components!
				++y;
			}

			ref readonly var meta = ref from.ComponentInfo[i];
			var leftArray = from._componentsData[i].AsSpan();
			var rightArray = to._componentsData[j].AsSpan();
			var insertComponent = rightArray.Slice(meta.Size * toRow, meta.Size);
			var removeComponent = leftArray.Slice(meta.Size * fromRow, meta.Size);
			var swapComponent = leftArray.Slice(meta.Size * (from._count - 1), meta.Size);
			removeComponent.CopyTo(insertComponent);
			swapComponent.CopyTo(removeComponent);
		}
	}

	private static void MakeEdges(Archetype left, Archetype right, EntityID id)
	{
		left._edgesRight.Add(new EcsEdge() { Archetype = right, ComponentID = id });
		right._edgesLeft.Add(new EcsEdge() { Archetype = left, ComponentID = id });
	}

	private void InsertVertex(Archetype newNode)
	{
		var nodeTypeLen = _components.Length;
		var newTypeLen = newNode._components.Length;

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

		if (!IsSuperset(newNode._components))
		{
			return;
		}

		var i = 0;
		var newNodeTypeLen = newNode._components.Length;
		for (; i < newNodeTypeLen && _components[i] == newNode._components[i]; ++i) { }

		MakeEdges(newNode, this, _components[i]);
	}

	private void ResizeComponentArray(int capacity)
	{
		for (int i = 0; i < _components.Length; ++i)
		{
			ref readonly var meta = ref ComponentInfo[i];
			Array.Resize(ref _componentsData[i], meta.Size * capacity);
			_capacity = capacity;
		}
	}

	internal bool IsSuperset(ReadOnlySpan<EntityID> other)
	{
		int i = 0, j = 0;
		while (i < _components.Length && j < other.Length)
		{
			if (_components[i] == other[j])
			{
				j++;
			}

			i++;
		}

		return j == other.Length;
	}

	internal int IsSuperset(ReadOnlySpan<Term> other)
	{
		int i = 0, j = 0, n = 0;
		while (i < _components.Length && j + n < other.Length)
		{
			if (_components[i] == other[j + n].ID)
			{
				if (other[j + n].Op != TermOp.With)
				{
					return -1;
				}

				j++;
			}
			else if (other[j + n].Op != TermOp.With)
			{
				n++;
				continue;
			}

			i++;
		}

		while (j + n < other.Length && other[j + n].Op != TermOp.With)
		{
			n++;
		}

		return j == other.Length - n ? 0 : 1;
	}


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<T> Field<T>() where T : unmanaged
	{
		var id = World.Component<T>();
		var size = TypeInfo<T>.Size;
		var column = GetComponentIndex(id);

		EcsAssert.Assert(column >= 0);

		return MemoryMarshal.Cast<byte, T>(_componentsData[column].AsSpan(0, size * Count));

		// ref var value = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(GetComponentRaw(id, 0, Count)));

		// EcsAssert.Assert(!Unsafe.IsNullRef(ref value));

		// return MemoryMarshal.CreateSpan(ref value, Count);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Has<T>() where T : unmanaged
	{
		var id = World.Component<T>();
		var column = GetComponentIndex(id);

		return column >= 0;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public EntityView Entity(int row)
		=> new (World, Entities[row]);
}

struct EcsEdge
{
	public EntityID ComponentID;
	public Archetype Archetype;
}
