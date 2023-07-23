namespace TinyEcs;

public readonly struct QueryBuilder : IEquatable<EntityID>, IEquatable<QueryBuilder>
{
	public readonly EntityID ID;
	internal readonly World World;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal QueryBuilder(World world, EntityID id)
	{
		World = world;
		ID = id;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(ulong other)
	{
		return ID == other;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(QueryBuilder other)
	{
		return ID == other.ID;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly QueryBuilder With<T>() where T : unmanaged
	{
		World.SetComponentData(ID, World.Component<T>() | EcsConst.ECS_QUERY_WITH, stackalloc byte[1]);
		return this;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly QueryBuilder With<TKind, TTarget>()
		where TKind : unmanaged
		where TTarget : unmanaged
	{
		return With(World.Component<TKind>(), World.Component<TTarget>());
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly QueryBuilder With(EntityID first, EntityID second)
	{
		var id = IDOp.Pair(first, second) | EcsConst.ECS_QUERY_WITH;
		World.SetComponentData(ID, id, stackalloc byte[1]);
		return this;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly QueryBuilder With(EntityID id)
	{
        World.SetComponentData(ID, id | EcsConst.ECS_QUERY_WITH, stackalloc byte[1]);
		return this;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly QueryBuilder Without<T>() where T : unmanaged
	{
		World.SetComponentData(ID, World.Component<T>() | EcsConst.ECS_QUERY_WITHOUT, stackalloc byte[1]);
		return this;
	}

	public QueryIterator GetEnumerator()
	{
		ref var record = ref World._entities.Get(ID);
		Debug.Assert(!Unsafe.IsNullRef(ref record));

		var components = record.Archetype.ComponentInfo;
		var cmps = ArrayPool<EntityID>.Shared.Rent(components.Length);

		var withIdx = 0;
		var withoutIdx = components.Length;

        for (int i = 0; i < components.Length; ++i)
		{
			ref readonly var meta = ref components[i];

			if ((meta.ID & EcsConst.ECS_QUERY_WITH) == EcsConst.ECS_QUERY_WITH)
			{
				cmps[withIdx++] = meta.ID  & ~EcsConst.ECS_QUERY_WITH;
			}
			else if ((meta.ID  & EcsConst.ECS_QUERY_WITHOUT) == EcsConst.ECS_QUERY_WITHOUT)
			{
				cmps[--withoutIdx] = meta.ID  & ~EcsConst.ECS_QUERY_WITHOUT;
			}
		}

		var with = cmps.AsSpan(0, withIdx);
		var without = cmps.AsSpan(0, components.Length).Slice(withoutIdx);

		if (with.IsEmpty)
		{
			return default;
		}

		with.Sort();
		without.Sort();

		var stack = new Stack<Archetype>();
		stack.Push(World._archRoot);

		return new QueryIterator(stack, cmps, with, without);
	}

	internal static unsafe Archetype? FetchArchetype
	(
		Stack<Archetype> stack,
		ReadOnlySpan<EntityID> with,
		ReadOnlySpan<EntityID> without
	)
	{
		if (stack.Count == 0 || !stack.TryPop(out var archetype) || archetype == null)
		{
			return null;
		}

		var span = CollectionsMarshal.AsSpan(archetype._edgesRight);
		if (!span.IsEmpty)
		{
			ref var start = ref MemoryMarshal.GetReference(span);
			ref var end = ref Unsafe.Add(ref start, span.Length);

			while (Unsafe.IsAddressLessThan(ref start, ref end))
			{
				if (without.IndexOf(start.ComponentID) < 0)
				{
					stack.Push(start.Archetype);
				}

				start = ref Unsafe.Add(ref start, 1);
			}
		}

		if (archetype.Count > 0 && archetype.IsSuperset(with))
		{
			// query ok, call the system now
			return archetype;
		}

		return null;
	}
}
