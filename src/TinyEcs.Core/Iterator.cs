namespace TinyEcs;

public delegate void IteratorDelegate<TContext>(ref Iterator<TContext> it);

public readonly ref struct Iterator<TContext>
{
	private readonly ReadOnlySpan<EcsID> _entities;
	private readonly ReadOnlySpan<int> _entitiesToTableRows;
	private readonly Table<TContext> _table;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal Iterator(Commands<TContext> commands, Archetype<TContext> archetype, object? userData, EcsID eventID = default)
	 : this(commands, archetype.Count, archetype.Table, archetype.Entities, archetype.EntitiesTableRows, userData, eventID)
	{

	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal Iterator(Commands<TContext> commands, int count, Table<TContext> table, ReadOnlySpan<EcsID> entities, ReadOnlySpan<int> toRows, object? userData, EcsID eventID = default)
	{
		Commands = commands;
		World = commands.World;
		UserData = userData;
		EventID = eventID;
		_table = table;
		_entities = entities;
		_entitiesToTableRows = toRows;
		Count = count;
		DeltaTime = commands.World.DeltaTime;
	}


	public readonly Commands<TContext> Commands { get; }
	public readonly World<TContext> World { get; }
	public readonly int Count { get; }
	public readonly float DeltaTime { get; }
	public readonly object? UserData { get; }
	public readonly EcsID EventID { get; }


	public unsafe readonly FieldIterator<T> Field<T>() where T : unmanaged, IComponent
	{
		ref var cmp = ref World.Component<T>();

		var column = _table.GetComponentIndex(ref cmp);
		EcsAssert.Assert(column >= 0);
		EcsAssert.Assert(cmp.Size == sizeof(T));

		var span = _table.ComponentData<T>(column, 0, _table.Rows);
		return new FieldIterator<T>(span, _entitiesToTableRows);
	}

	public readonly bool Has<T>() where T : unmanaged, IComponentStub
	{
		ref var cmp = ref World.Component<T>();
		var column = _table.GetComponentIndex(ref cmp);
		return column >= 0;
	}

	public readonly EntityView<TContext> Entity(int row)
		=> World.Entity(_entities[row]);

	public readonly CommandEntityView<TContext> EntityDeferred(int row)
		=> Commands.Entity(_entities[row]);
}


[SkipLocalsInit]
public readonly ref struct FieldIterator<T> where T : unmanaged, IComponent
{
	private readonly ref T _firstElement;
	private readonly ref int _firstEntity;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal FieldIterator(Span<T> elements, ReadOnlySpan<int> entities)
	{
		_firstElement = ref MemoryMarshal.GetReference(elements);
		_firstEntity = ref MemoryMarshal.GetReference(entities);
	}

	public readonly ref T this[int index]
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => ref Unsafe.Add(ref _firstElement, Unsafe.Add(ref _firstEntity, index));
	}
}
