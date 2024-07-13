using System.Collections.Concurrent;

namespace TinyEcs;

public sealed partial class World
{
	private readonly ConcurrentQueue<DeferredOp> _operations = new();
	private WorldState _worldState = new () { Locks = 0 };

	public bool IsDeferred => _worldState.Locks > 0;



	public void BeginDeferred()
	{
		if (_worldState.Locks < 0)
			return;

		_worldState.Locks += 1;
	}

	public void EndDeferred()
	{
		if (_worldState.Locks < 0)
			return;

		_worldState.Locks -= 1;

		if (_worldState.Locks == 0)
		{
			_worldState.Locks = -1;
			Merge();
			_worldState.Locks = 0;
		}
	}


	private void SetDeferred<T>(EcsID entity) where T : struct
	{
		ref readonly var cmp = ref Component<T>();

		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.SetComponent,
			Entity = entity,
			Data = null!,
			ComponentInfo = cmp
		};

		_operations.Enqueue(cmd);
	}

	private ref T SetDeferred<T>(EcsID entity, T component) where T : struct
	{
		ref readonly var cmp = ref Component<T>();

		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.SetComponent,
			Entity = entity,
			Data = component,
			ComponentInfo = cmp
		};

		_operations.Enqueue(cmd);

		return ref Unsafe.Unbox<T>(cmd.Data);
	}

	private object? SetDeferred(EcsID entity, EcsID id, object? rawCmp, int size)
	{
		var cmp = Lookup.GetComponent(id, size);

		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.SetComponent,
			Entity = entity,
			Data = rawCmp,
			ComponentInfo = cmp
		};

		_operations.Enqueue(cmd);
		return rawCmp;
	}

	private void UnsetDeferred<T>(EcsID entity) where T : struct
	{
		ref readonly var cmp = ref Component<T>();

		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.UnsetComponent,
			Entity = entity,
			ComponentInfo = cmp
		};

		_operations.Enqueue(cmd);
	}

	private void UnsetDeferred(EcsID entity, EcsID id)
	{
		var cmp = new ComponentInfo(id, 0);

		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.UnsetComponent,
			Entity = entity,
			ComponentInfo = cmp
		};

		_operations.Enqueue(cmd);
	}

	private void DeleteDeferred(EcsID entity)
	{
		var cmd = new DeferredOp()
		{
			Op = DeferredOpTypes.DestroyEntity,
			Entity = entity
		};

		_operations.Enqueue(cmd);
	}


	private void Merge()
	{
		while (!_operations.IsEmpty && _operations.TryDequeue(out var op))
		{
			switch (op.Op)
			{
				case DeferredOpTypes.DestroyEntity:
					Delete(op.Entity);
					break;

				case DeferredOpTypes.SetComponent:
				{
					if (op.ComponentInfo.ID.IsPair())
					{
						(var first, var second) = op.ComponentInfo.ID.Pair();
						CheckUnique(op.Entity, first);
						CheckSymmetric(op.Entity, first, second);
					}

					(var array, var row) = AttachComponent(op.Entity, op.ComponentInfo.ID, op.ComponentInfo.Size);
					array?.SetValue(op.Data, row & Archetype.CHUNK_THRESHOLD);

					break;
				}

				case DeferredOpTypes.UnsetComponent:
				{
					DetachComponent(op.Entity, op.ComponentInfo.ID);

					break;
				}
			}
		}

	}


	struct WorldState
	{
		public int Locks;
	}

	struct DeferredOp
	{
		public DeferredOpTypes Op;
		public EcsID Entity;
		public ComponentInfo ComponentInfo;
		public object? Data;
	}

	enum DeferredOpTypes : byte
	{
		DestroyEntity,
		SetComponent,
		UnsetComponent,
	}
}
