namespace TinyEcs;

public sealed partial class World : IDisposable
{
	const ulong ECS_MAX_COMPONENT_FAST_ID = 256;
	const ulong ECS_START_USER_ENTITY_DEFINE = ECS_MAX_COMPONENT_FAST_ID;


	private readonly Archetype _archRoot;
	private readonly EntitySparseSet<EcsRecord> _entities = new();
	private readonly Dictionary<EcsID, Archetype> _typeIndex = new ();
	private readonly Dictionary<EcsID, Table> _tableIndex = new ();
	private readonly ComponentComparer _comparer;
	private readonly Commands _commands;
	private int _frame;
	private EcsID _lastCompID = 1;


	internal World()
	{
		_comparer = new ComponentComparer(this);
		_archRoot = new Archetype(this, new (0, ReadOnlySpan<EcsComponent>.Empty, _comparer), ReadOnlySpan<EcsComponent>.Empty, _comparer);
		_commands = new (this);

		InitializeDefaults();
		_entities.MaxID = ECS_START_USER_ENTITY_DEFINE;
	}


	public int EntityCount => _entities.Length;

	public float DeltaTime { get; private set; }



	public void Dispose()
	{
		_entities.Clear();
		_archRoot.Clear();
		_typeIndex.Clear();
		_tableIndex.Clear();
		_commands.Clear();
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

	internal unsafe EntityView CreateComponent(EcsID id, int size)
	{
		if (id == 0)
		{
			id = NewEmpty();
		}
		else if (Exists(id) && Has<EcsComponent>(id))
		{
			ref var cmp = ref Get<EcsComponent>(id);
			EcsAssert.Panic(cmp.Size == size);

			return Entity(id);
		}

		size = Math.Max(size, 0);

		var cmp2 = new EcsComponent(id, size);
		Set(id, cmp2);
		Set(id, EcsPanic, EcsDelete);

		if (size == 0)
			Set(id, EcsTag);

		return Entity(id);
	}

	private unsafe int GetSize<T>() where T : unmanaged, IComponentStub
		 => typeof(T).IsAssignableTo(typeof(ITag)) ? 0 : sizeof(T);

	internal unsafe ref EcsComponent Component<T>() where T : unmanaged, IComponentStub
	{
		ref var lookup = ref Lookup.Entity<T>.Component;

		if (lookup.ID == 0 || !Exists(lookup.ID))
		{
			EcsID id = lookup.ID;
			if (id == 0 && _lastCompID < ECS_MAX_COMPONENT_FAST_ID)
			{
				do {
					id = _lastCompID++;
				} while (Exists(id) && id <= ECS_MAX_COMPONENT_FAST_ID);
			}

			id = Entity(id);
			var size = GetSize<T>();
			lookup = new EcsComponent(id, size);
			_ = CreateComponent(id, size);

			Console.WriteLine("created {0} - {1}", Lookup.Entity<T>.Name, id);
		}

		// if (id > 0)
		// 	EcsAssert.Assert(lookup.ID == id);

		return ref lookup;
	}

	public EcsID Pair<TKind, TTarget>()
	where TKind : unmanaged, ITag
	where TTarget : unmanaged, IComponentStub
		=> IDOp.Pair(Entity<TKind>(), Entity<TTarget>());

	public EcsID Pair<TKind>(EcsID target)
	where TKind : unmanaged, ITag
		=> IDOp.Pair(Entity<TKind>(), target);

	public Query Query()
		=> new (this);

	public unsafe EntityView Event(delegate*<ref Iterator, void> callback, ReadOnlySpan<Term> terms, ReadOnlySpan<EcsID> events)
	{
		var obs = Entity()
			.Set(new EcsEvent(callback, terms));

		foreach (ref readonly var id in events)
			obs.Set(id);

		return obs;
	}

	public EntityView Entity(ReadOnlySpan<char> name)
	{
		// TODO
		EcsID id = 0;

		return Entity(id);
	}

	public EntityView Entity(EcsID id = default)
	{
		return id == 0 || !Exists(id) ? NewEmpty(id) : new(this, id);
	}

	public EntityView Entity<T>(EcsID id = default)
	where T : unmanaged, IComponentStub
	{
		return Entity(Component<T>().ID);
	}

	public EntityView Entity<TKind, TTarget>()
	where TKind : unmanaged, ITag
	where TTarget : unmanaged, IComponentStub
		=> Entity(IDOp.Pair(Entity<TKind>(), Entity<TTarget>()));

	internal EntityView NewEmpty(ulong id = 0)
	{
		ref var record = ref (id > 0 ? ref _entities.Add(id, default!) : ref _entities.CreateNew(out id));
		record.Archetype = _archRoot;
		record.Row = _archRoot.Add(id).Item1;

		return new (this, id);
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

		EcsAssert.Panic(!Has<EcsPanic, EcsDelete>(entity), $"You cannot delete entity {entity} with ({nameof(EcsPanic)}, {nameof(EcsDelete)})");

		Query()
			.With<EcsChildOf>(entity)
			.Iterate(static (ref Iterator it) => {
				for (int i = 0; i < it.Count; ++i)
				{
					it.Entity(0).Delete();
				}
			});

		var removedId = record.Archetype.Remove(ref record);
		EcsAssert.Assert(removedId == entity);

		var last = record.Archetype.Entities[record.Row];
		_entities.Get(last) = record;
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

	internal void DetachComponent(EcsID entity, ref EcsComponent cmp)
	{
		ref var record = ref GetRecord(entity);
		InternalAttachDetach(entity, ref record, ref cmp, false);
	}

	private bool InternalAttachDetach(EcsID entity, ref EcsRecord record, ref EcsComponent cmp, bool add)
	{
		EcsAssert.Assert(!Unsafe.IsNullRef(ref record));

		var arch = CreateArchetype(record.Archetype, ref cmp, add);
		if (arch == null)
			return false;

		if (!add)
		{
			EmitEvent<EcsEventOnUnset>(entity, cmp.ID);
		}

		record.Row = record.Archetype.MoveEntity(arch, record.Row);
		record.Archetype = arch!;

		return true;
	}

	[SkipLocalsInit]
	internal Archetype? CreateArchetype(Archetype root, ref EcsComponent cmp, bool add)
	{
		// var column = root.GetComponentIndex(ref cmp);

		// if (add && column >= 0)
		// {
		// 	return null;
		// }
		// else if (!add && column < 0)
		// {
		// 	return null;
		// }

		if (!add && root.GetComponentIndex(ref cmp) < 0)
			return null;

		var initType = root.ComponentInfo;
		var cmpCount = Math.Max(0, initType.Length + (add ? 1 : -1));

		const int STACKALLOC_SIZE = 16;

		EcsComponent[]? buffer = null;
		Span<EcsComponent> span = cmpCount <= STACKALLOC_SIZE ?
		 stackalloc EcsComponent[STACKALLOC_SIZE] :
		 (buffer = ArrayPool<EcsComponent>.Shared.Rent(cmpCount));

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

		var hash = Hash(span, false);

		ref var arch = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeIndex, hash, out var exists);
		if (!exists)
		{
			ref var table = ref Unsafe.NullRef<Table>();

			if (cmp.Size != 0)
			{
				var tableHash = Hash(span, true);
				table = ref CollectionsMarshal.GetValueRefOrAddDefault(_tableIndex, tableHash, out exists)!;
				if (!exists)
				{
					table = new (tableHash, span, _comparer);
				}
			}
			else
			{
				table = ref Unsafe.AsRef(root.Table)!;
			}

			arch = _archRoot.InsertVertex(root, table, span, ref cmp);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static EcsID Hash(UnsafeSpan<EcsComponent> components, bool checkSize)
		{
			unchecked
			{
				EcsID hash = 5381;

				while (components.CanAdvance())
				{
					if (!checkSize || components.Value.Size > 0)
						hash = ((hash << 5) + hash) + components.Value.ID;

					components.Advance();
				}

				return hash;
			}
		}

		if (buffer != null)
		{
			ArrayPool<EcsComponent>.Shared.Return(buffer);
		}

		return arch;
	}

	internal void Set(EcsID entity, ref EcsComponent cmp, ReadOnlySpan<byte> data)
	{
		EcsAssert.Assert(cmp.Size == data.Length);

		ref var record = ref GetRecord(entity);

		var emit = false;
		var column = record.Archetype.GetComponentIndex(ref cmp);
		if (column < 0)
		{
			emit = InternalAttachDetach(entity, ref record, ref cmp, true);
			column = record.Archetype.GetComponentIndex(ref cmp);
		}

		if (cmp.Size > 0)
		{
			var buf = record.Archetype.Table.ComponentData<byte>
			(
				column,
				record.Archetype.EntitiesTableRows[record.Row] * cmp.Size,
				cmp.Size
			);

			EcsAssert.Assert(data.Length == buf.Length);
			data.CopyTo(buf);
		}

		if (emit)
		{
			EmitEvent<EcsEventOnSet>(entity, cmp.ID);
		}
	}

	[SkipLocalsInit]
	public unsafe void EmitEvent(EcsID eventID, EcsID entity, EcsID component)
	{
		EcsAssert.Assert(Exists(eventID));
		EcsAssert.Assert(Exists(entity));
		EcsAssert.Assert(Exists(component));

		Query
		(
			stackalloc Term[] {
				Term.With(Entity<EcsEvent>()),
				Term.With(eventID),
			},
			&OnEvent,
			new ObserverInfo() {
				Entity = entity,
				Event = eventID,
				LastComponent = Term.With(component)
			}
		);
	}

	private struct ObserverInfo
	{
		public EcsID Entity;
		public EcsID Event;
		public Term LastComponent;
	}

	static unsafe void OnEvent(ref Iterator it)
	{
		ref var eventInfo = ref Unsafe.Unbox<ObserverInfo>(it.UserData!);
		ref var record = ref it.World.GetRecord(eventInfo.Entity);
		var iterator = new Iterator
		(
			it.Commands,
			1,
			record.Archetype.Table,
			stackalloc EcsID[1] { eventInfo.Entity },
			stackalloc int[1] { record.Archetype.EntitiesTableRows[record.Row] },
			null,
			eventInfo.Event
		);

		var evA = it.Field<EcsEvent>();

		for (int i = 0; i < it.Count; ++i)
		{
			ref var ev = ref evA[i];

			if (record.Archetype.FindMatch(ev.Terms) == 0 &&
			    ev.Terms.BinarySearch(eventInfo.LastComponent, it.World._comparer) >= 0)
			{
				ev.Callback(ref iterator);
			}
		}
	}

	internal bool Has(EcsID entity, ref EcsComponent cmp)
	{
		ref var record = ref GetRecord(entity);
		return record.Archetype.GetComponentIndex(ref cmp) >= 0;
	}

	public bool Has(EcsID entity, EcsID tag)
	{
		var cmp = new EcsComponent(tag, 0);
		return Has(entity, ref cmp);
	}

	public void Set(EcsID entity, EcsID first, EcsID second)
	{
		var id = IDOp.Pair(first, second);

		if (Has(first, EcsExclusive))
		{
			ref var record = ref GetRecord(entity);
			var id2 = IDOp.Pair(first, Entity<EcsAny>());
			var cmp3 = new EcsComponent(id2, 0);
			var column = record.Archetype.GetComponentIndex(ref cmp3);

			if (column >= 0)
			{
				DetachComponent(entity, ref record.Archetype.ComponentInfo[column]);
			}
		}

		var cmp = new EcsComponent(id, 0);
		Set(entity, ref cmp, ReadOnlySpan<byte>.Empty);
	}

	public void Set(EcsID entity, EcsID tag)
	{
		EcsAssert.Assert(!IDOp.IsPair(tag));

		if (Exists(tag) && Has<EcsComponent>(tag))
		{
			ref var cmp2 = ref Get<EcsComponent>(tag);
			Set(entity, ref cmp2, ReadOnlySpan<byte>.Empty);

			return;
		}

		var cmp = new EcsComponent(tag, 0);
		Set(entity, ref cmp, ReadOnlySpan<byte>.Empty);
	}

	public bool Has(EcsID entity, EcsID first, EcsID second)
	{
		var id = IDOp.Pair(first, second);
		var cmp = new EcsComponent(id, 0);
		return Has(entity, ref cmp);
	}

	public EcsID GetParent(EcsID id)
	{
		ref var record = ref GetRecord(id);

		var pair = Pair<EcsChildOf, EcsAny>();
		var cmp = new EcsComponent(pair, 0);
		var column = record.Archetype.GetComponentIndex(ref cmp);

		if (column >= 0)
		{
			ref var meta = ref record.Archetype.ComponentInfo[column];

			return IDOp.GetPairSecond(meta.ID);
		}

		return 0;
	}

	public ReadOnlySpan<EcsComponent> GetType(EcsID id)
	{
		ref var record = ref GetRecord(id);
		return record.Archetype.ComponentInfo;
	}

	public void PrintGraph()
	{
		_archRoot.Print();
	}

	[SkipLocalsInit]
	public unsafe void RunPhase(EcsID phase)
	{
		Span<Term> terms = stackalloc Term[] {
			Term.With(Entity<EcsSystem>()),
			Term.With(phase),
		};

		Query(terms, &RunSystems);
	}

	public void Step(float deltaTime = 0.0f)
	{
		DeltaTime = deltaTime;

		_commands.Merge();

		if (_frame == 0)
		{
			RunPhase(Pair<EcsPhase, EcsSystemPhasePreStartup>());
			RunPhase(Pair<EcsPhase, EcsSystemPhaseOnStartup>());
			RunPhase(Pair<EcsPhase, EcsSystemPhasePostStartup>());
		}

		RunPhase(Pair<EcsPhase, EcsSystemPhasePreUpdate>());
		RunPhase(Pair<EcsPhase, EcsSystemPhaseOnUpdate>());
		RunPhase(Pair<EcsPhase, EcsSystemPhasePostUpdate>());

		_commands.Merge();
		_frame += 1;

		if (_frame % 10 == 0)
		{
			Optimize();
		}
	}

	static unsafe void RunSystems(ref Iterator it)
	{
		var emptyIt = new Iterator(it.Commands, 0, it.World._archRoot.Table, ReadOnlySpan<EcsID>.Empty, ReadOnlySpan<int>.Empty, null, 0);
		var sysA = it.Field<EcsSystem>();

		for (int i = 0; i < it.Count; ++i)
		{
			ref var sys = ref sysA[i];

			if (!float.IsNaN(sys.Tick))
			{
				// TODO: check for it.DeltaTime > 0?
				sys.TickCurrent += it.DeltaTime;

				if (sys.TickCurrent < sys.Tick)
				{
					continue;
				}

				sys.TickCurrent = 0;
			}

			if (sys.Query != 0)
			{
				it.World.Query(sys.Terms, sys.Callback);
			}
			else
			{
				sys.Callback(ref emptyIt);
			}
		}
	}

	public unsafe void Query
	(
		Span<Term> terms,
		delegate* <ref Iterator, void> action,
		object? userData = null
	)
	{
		terms.Sort(static (a, b) => a.ID.CompareTo(b.ID));

		QueryRec(_archRoot, terms, _commands, action, userData);

		static void QueryRec
		(
			Archetype root,
			UnsafeSpan<Term> terms,
			Commands commands,
			delegate* <ref Iterator, void> action,
			object? userData
		)
		{
			var result = root.FindMatch(terms);
			if (result < 0)
			{
				return;
			}

			if (result == 0 && root.Count > 0)
			{
				var it = new Iterator(commands, root, userData);
				action(ref it);
			}

			var span = CollectionsMarshal.AsSpan(root._edgesRight);
			if (span.IsEmpty)
			{
				return;
			}

			ref var start = ref MemoryMarshal.GetReference(span);
			ref var end = ref Unsafe.Add(ref start, span.Length);

			while (Unsafe.IsAddressLessThan(ref start, ref end))
			{
				QueryRec(start.Archetype, terms, commands, action, userData);

				start = ref Unsafe.Add(ref start, 1);
			}
		}
	}
}

internal static class Lookup
{
	internal static class Entity<T> where T : unmanaged, IComponentStub
	{
		public static EcsComponent Component;
		public static readonly string Name = typeof(T).ToString();
	}
}


struct EcsRecord
{
	public Archetype Archetype;
	public int Row;
}
