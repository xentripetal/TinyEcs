﻿using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EntityID = System.UInt64;

namespace TinyEcs;

public sealed partial class World : IDisposable
{
    private int _entityCount = 0;
    private int _deferStatus, _deferActionCount;
    private EcsDeferredAction[] _deferredActions = new EcsDeferredAction[0xFF];

    private Archetype _archRoot;
    internal readonly ComponentStorage _storage;

    internal readonly Dictionary<int, Archetype> _typeIndex = new Dictionary<int, Archetype>();
    private readonly Dictionary<EntityID, EcsSystem> _systemIndex = new Dictionary<EntityID, EcsSystem>();
    private readonly SparseSet<EcsRecord> _entities = new SparseSet<EcsRecord>(0);


    public World()
    {
        _storage = new ComponentStorage(this);
        _archRoot = new Archetype(this, new EcsSignature(0));
    }


    public int EntityCount => _entityCount;


    private unsafe void Destroy()
    {
        foreach ((var type, var arch) in _typeIndex)
        {
            arch.Signature.Dispose();
        }

        _systemIndex.Clear();
        _typeIndex.Clear();

        _entities.Clear();
        _storage.Clear();
        _entityCount = 0;
        _archRoot = new Archetype(this, new EcsSignature(0));
    }

    public void Dispose() => Destroy();

    public unsafe void Step()
    {
        foreach ((var id, EcsSystem system) in _systemIndex)
        {
            foreach (var it in system.Query)
            {
                system.Func(in it);
            }
        }
    }

    public IQueryComposition Query() => new Query(this);

    public EntityID CreateEntity()
    {
        var id = NewID();
        var row = _archRoot.Add(id);
        ref var record = ref _entities.Add((int)IDOp.RealID(id), default);
        record.Archetype = _archRoot;
        record.Row = row;

        Interlocked.Increment(ref _entityCount);

        //if (IsDeferred())
        //{
        //    CreateDeferred(id);
        //}
        //else
        //{
        //    InternalCreateEntity(id);
        //}

        return id;
    }

    public void DestroyEntity(EntityID entity)
    {
        if (IsDeferred())
        {
            DestroyDeferred(entity);
        }
        else
        {
            ref var record = ref _entities[(int) IDOp.RealID(entity)];
            if (Unsafe.IsNullRef(ref record))
            {
                Debug.Fail("not an entity!");
            }

            var removedId = record.Archetype.Remove(record.Row);

            Debug.Assert(removedId == entity);

            _entities.Remove((int) IDOp.RealID(removedId));

            Interlocked.Decrement(ref _entityCount);
        }
    }

    public bool IsEntityAlive(EntityID entity) => IDOp.GetGeneration(entity) > 0;

    private int Attach(EntityID entity, int componentID)
    {
        if (IsDeferred())
        {
            AttachDeferred(entity, componentID);
            return componentID;
        }

        ref var record = ref _entities[(int)IDOp.RealID(entity)];
        if (Unsafe.IsNullRef(ref record))
        {
            Debug.Fail("not an entity!");
        }

        var column = componentID >= record.Archetype.Lookup.Length ? -1 : record.Archetype.Lookup[componentID];
        if (column >= 0)
        {
            return -1;
        }

        InternalAttachDetach(ref record, componentID, true);
        return componentID;
    }

    private int Detach(EntityID entity, int componentID)
    {
        if (IsDeferred())
        {
            DetachDeferred(entity, componentID);
            return componentID;
        }

        ref var record = ref _entities[(int)IDOp.RealID(entity)];
        if (Unsafe.IsNullRef(ref record))
        {
            Debug.Fail("not an entity!");
        }

        var column = componentID >= record.Archetype.Lookup.Length ? -1 : record.Archetype.Lookup[componentID];
        if (column < 0)
        {
            return -1;
        }

        InternalAttachDetach(ref record, componentID, false);
        return componentID;
    }



    private void InternalCreateEntity(EntityID id)
    {
        var row = _archRoot.Add(id);
        ref var record = ref _entities.Add((int)IDOp.RealID(id), default);
        record.Archetype = _archRoot;
        record.Row = row;

        Interlocked.Increment(ref _entityCount);
    }

    private void InternalAttachDetach(ref EcsRecord record, int componentID, bool add)
    {
        var initType = record.Archetype.Signature;

        var cmpCount = Math.Max(0, initType.Count + (add ? 1 : -1));
        Span<int> span = stackalloc int[cmpCount];

        if (!add)
        {
            for (int i = 0, j = 0; i < initType.Count; ++i)
            {
                if (initType[i] != componentID)
                {
                    span[j++] = initType[i];
                }
            }
        }
        else if (!span.IsEmpty)
        {
            initType.Components.CopyTo(span);
            span[^1] = componentID;
        }

        span.Sort();

        var hash = ComponentHasher.Calculate(span);

        ref var arch = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeIndex, hash, out var exists);
        if (!exists)
        {
            var finiType = new EcsSignature(initType);
            if (add)
                finiType.Add(componentID);
            else
                finiType.Remove(componentID);

            arch = _archRoot.InsertVertex(record.Archetype, finiType, componentID);
        }

        var newRow = Archetype.MoveEntity(record.Archetype, arch!, record.Row);
        record.Row = newRow;
        record.Archetype = arch!;
    }

    private void Set(EntityID entity, int componentID, ReadOnlySpan<byte> data)
    {
        ref var record = ref _entities[(int)IDOp.RealID(entity)];
        if (Unsafe.IsNullRef(ref record))
        {
            Debug.Fail("not an entity!");
        }

        var column = componentID >= record.Archetype.Lookup.Length ? -1 : record.Archetype.Lookup[componentID];
        if (column == -1)
        {
            Attach(entity, componentID);
            column = componentID >= record.Archetype.Lookup.Length ? -1 : record.Archetype.Lookup[componentID];
            //return;
        }

        var size = record.Archetype.Sizes[componentID];
        var componentData = record.Archetype._components[column]
            .AsSpan(size * record.Row, size);
        data.CopyTo(componentData);
    }

    private bool Has(EntityID entity, int componentID) 
        => !Get(entity, componentID).IsEmpty;

    private Span<byte> Get(EntityID entity, int componentID)
    {
        ref var record = ref _entities[(int)IDOp.RealID(entity)];
        if (Unsafe.IsNullRef(ref record))
        {
            return Span<byte>.Empty;
        }

        var column = componentID >= record.Archetype.Lookup.Length ? -1 : record.Archetype.Lookup[componentID];
        if (column == -1)
        {
            return Span<byte>.Empty;
        }

        var size = record.Archetype.Sizes[componentID];

        return record.Archetype._components[column]
            .AsSpan(size * record.Row, size);
    }

    public unsafe ulong RegisterSystem(IQueryComposition query, CallbackIterator func)
    {
        var id = CreateEntity();
        ref var sys = ref CollectionsMarshal.GetValueRefOrAddDefault(_systemIndex, id, out var exists);
        Debug.Assert(!exists);
        sys = new EcsSystem(query, func);
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityID NewID()
    {
        var recycle = _entities.Length < _entities._dense.Reserved;

        if (recycle)
        {
            var id = _entities._dense[_entities._dense.Reserved - 1];
            if (id > 0 && !_entities.Contains(id))
            {
                return (EntityID) id;
            }
        }

        return (EntityID)(_entities.Length + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsDeferred() => _deferStatus != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginDefer() => Interlocked.Increment(ref _deferStatus);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndDefer() => Interlocked.Decrement(ref _deferStatus);

    private void CreateDeferred(EntityID entity)
    {
        ref var cmd = ref PeekDeferredCommand();
        cmd.Action = DeferredOp.Create;
        cmd.Stage = _deferStatus;
        cmd.Create.Entity = entity;
    }

    private void DestroyDeferred(EntityID entity)
    {
        ref var cmd = ref PeekDeferredCommand();
        cmd.Action = DeferredOp.Destroy;
        cmd.Stage = _deferStatus;
        cmd.Destroy.Entity = entity;
    }

    private void AttachDeferred(EntityID entity, int component)
    {
        ref var cmd = ref PeekDeferredCommand();
        cmd.Action = DeferredOp.Attach;
        cmd.Stage = _deferStatus;
        cmd.Attach.Entity = entity;
        cmd.Attach.Component = component;
    }

    private void DetachDeferred(EntityID entity, int component)
    {
        ref var cmd = ref PeekDeferredCommand();
        cmd.Action = DeferredOp.Detach;
        cmd.Stage = _deferStatus;
        cmd.Detach.Entity = entity;
        cmd.Detach.Component = component;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref EcsDeferredAction PeekDeferredCommand()
    {
        if (_deferActionCount >= _deferredActions.Length)
        {
            Array.Resize(ref _deferredActions, _deferredActions.Length * 2);
        }

        return ref _deferredActions[_deferActionCount++];
    }

    internal void MergeDeferred()
    {
        // TODO: test for nested queries
        if (IsDeferred())
            return;

        var count = _deferActionCount;

        for (int i = 0; i < count; ++i)
        {
            ref var cmd = ref _deferredActions[i];

            switch (cmd.Action)
            {
                case DeferredOp.Create:

                    InternalCreateEntity(cmd.Create.Entity);

                    break;

                case DeferredOp.Destroy:

                    DestroyEntity(cmd.Create.Entity);

                    break;

                case DeferredOp.Attach:

                    Attach(cmd.Attach.Entity, cmd.Attach.Component);

                    break;

                case DeferredOp.Detach:

                    Detach(cmd.Detach.Entity, cmd.Detach.Component);

                    break;
            }
        }

        _deferActionCount -= count;
    }

    private enum DeferredOp : byte
    {
        Create,
        Destroy,
        Attach,
        Detach,
    }

    [StructLayout(LayoutKind.Explicit)]
    struct EcsDeferredAction
    {
        [FieldOffset(0)]
        public DeferredOp Action;

        [FieldOffset(1)]
        public int Stage;

        [FieldOffset(1)]
        public EcsDeferredCreateEntity Create;

        [FieldOffset(1)]
        public EcsDeferredDestroyEntity Destroy;

        [FieldOffset(1)]
        public EcsDeferredAttachComponent Attach;

        [FieldOffset(1)]
        public EcsDeferredDetachComponent Detach;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EcsDeferredCreateEntity
    {
        public DeferredOp Action;
        public int Stage;

        public EntityID Entity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EcsDeferredDestroyEntity
    {
        public DeferredOp Action;
        public int Stage;

        public EntityID Entity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EcsDeferredAttachComponent
    {
        public DeferredOp Action;
        public int Stage;

        public EntityID Entity;
        public int Component;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EcsDeferredDetachComponent
    {
        public DeferredOp Action;
        public int Stage;

        public EntityID Entity;
        public int Component;
    }




    internal sealed class ComponentStorage
    {
        const int INITIAL_LENGTH = 32;

        private readonly World _world;
        private int[] _componentsHashes = new int[INITIAL_LENGTH];
        private EntityID[] _componentsIDs = new EntityID[INITIAL_LENGTH];
        private int[] _componentsSizes = new int[INITIAL_LENGTH];
        private readonly Dictionary<EntityID, int> _IDsToGlobal = new Dictionary<EntityID, int>();
        //private readonly Dictionary<int, int> _globalToIDs = new Dictionary<int, int>();

        public ComponentStorage(World world)
        {
            _world = world;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetOrCreateID<T>() where T : struct
        {
            var globalIndex = TypeOf<T>.GlobalIndex;

            GrowIfNecessary(globalIndex);

            if (_componentsHashes[globalIndex] == 0)
            {
                _componentsHashes[globalIndex] = TypeOf<T>.Hash;
                _componentsIDs[globalIndex] = _world.CreateEntity();
                _componentsSizes[globalIndex] = TypeOf<T>.Size;

                _IDsToGlobal[_componentsIDs[globalIndex]] = globalIndex;
                //_IDsToGlobal[globalIndex] = _componentsIDs[globalIndex];

                //_globalToIDs[globalIndex] = _componentsIDs[globalIndex];
            }

            return globalIndex; // _componentsIDs[globalIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetID<T>() where T : struct
        {
            return TypeOf<T>.GlobalIndex; // _componentsIDs[TypeOf<T>.GlobalIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetGlobalID(EntityID componentID)
        {
            ref var globalIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_IDsToGlobal, componentID);
            if (Unsafe.IsNullRef(ref globalIndex))
            {
                Debug.Fail("invalid componentID");
            }

            return globalIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSize(int componentID)
        {
            //ref var globalIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_IDsToGlobal, componentID);
            //if (Unsafe.IsNullRef(ref globalIndex))
            //{
            //    Debug.Fail("invalid componentID");
            //}

            return _componentsSizes[componentID];
        }

        public void Clear()
        {
            _componentsHashes.AsSpan().Clear();
            _componentsIDs.AsSpan().Clear();
            _componentsSizes.AsSpan().Clear();
            _IDsToGlobal.Clear();
        }

        private void GrowIfNecessary(int current)
        {
            if (current == _componentsHashes.Length)
            {
                var capacity = _componentsHashes.Length * 2;

                Array.Resize(ref _componentsIDs, capacity);
                Array.Resize(ref _componentsHashes, capacity);
                Array.Resize(ref _componentsSizes, capacity);
            }
        }

        [SkipLocalsInit]
        static class TypeOf<T> where T : struct
        {
            public static int GlobalIndex = GlobalIDGen.Next();
            public static int Size = Unsafe.SizeOf<T>();
            public static int Hash = typeof(T).GetHashCode();
        }

        static class GlobalIDGen
        {
            private static int _next = -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Next()
            {
                Interlocked.Increment(ref _next);
                return _next;
            }
        }
    }
}

sealed unsafe class Archetype
{
    const int ARCHETYPE_INITIAL_CAPACITY = 16;

    private readonly World _world;
    private int _capacity, _count;
    private EntityID[] _entityIDs;
    internal byte[][] _components;
    private readonly EcsSignature _sign;
    internal List<EcsEdge> _edgesLeft, _edgesRight;
    private readonly int[] _lookup, _sizes;

    public Archetype(World world, EcsSignature sign)
    {
        _world = world;
        _capacity = ARCHETYPE_INITIAL_CAPACITY;
        _count = 0;
        _sign = sign;
        _entityIDs = new EntityID[ARCHETYPE_INITIAL_CAPACITY];
        _components = new byte[sign.Count][];
        _edgesLeft = new List<EcsEdge>();
        _edgesRight = new List<EcsEdge>();

        var maxID = -1;
        for (int i = 0; i < sign.Count; ++i)
        {
            maxID = Math.Max(maxID, sign[i]);
        }

        _lookup = new int[maxID + 1];
        _sizes = new int[maxID + 1];
        _lookup.AsSpan().Fill(-1);
        for (int i = 0; i < sign.Count; ++i)
        {
            _lookup[sign[i]] = i;
            _sizes[sign[i]] = world._storage.GetSize(sign[i]);
        }

        ResizeComponentArray(ARCHETYPE_INITIAL_CAPACITY);
    }


    public EcsSignature Signature => _sign;
    public int Count => _count;
    public EntityID[] Entities => _entityIDs;
    public int[] Lookup => _lookup;
    public int[] Sizes => _sizes;


    public int Add(EntityID entityID)
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

    public EntityID Remove(int row)
    {
        var removed = _entityIDs[row];
        _entityIDs[row] = _entityIDs[_count - 1];

        for (int i = 0; i < _sign.Count; ++i)
        {
            var size = _sizes[_sign[i]];
            var leftArray = _components[i].AsSpan();

            var removeComponent = leftArray.Slice(size * row, size);
            var swapComponent = leftArray.Slice(size * (_count - 1), size);

            swapComponent.CopyTo(removeComponent);
        }

        --_count;

        return removed;
    }

    public Archetype InsertVertex(Archetype left, EcsSignature newType, int componentID)
    {
        var vertex = new Archetype(left._world, newType);
        MakeEdges(left, vertex, componentID);
        InsertVertex(vertex);
        return vertex;
    }

    public static int MoveEntity(Archetype from, Archetype to, int fromRow)
    {
        var removed = from._entityIDs[fromRow];
        from._entityIDs[fromRow] = from._entityIDs[from._count - 1];

        var toRow = to.Add(removed);

        Copy(from, fromRow, to, toRow);

        --from._count;

        return toRow;
    }

    static void Copy(Archetype from, int fromRow, Archetype to, int toRow)
    {
        var isLeft = to._sign.Count < from._sign.Count;
        int i = 0, j = 0;
        var count = isLeft ? to._sign.Count : from._sign.Count;

        ref var x = ref (isLeft ? ref j : ref i);
        ref var y = ref (!isLeft ? ref j : ref i);

        for (; /*(isLeft ? j : i)*/ x < count; ++x)
        {
            while (from._sign[i] != to._sign[j])
            {
                // advance the sign with less components!
                ++y;
            }

            var size = from.Sizes[from._sign[i]];
            var leftArray = from._components[i].AsSpan();
            var rightArray = to._components[j].AsSpan();
            var insertComponent = rightArray.Slice(size * toRow, size);
            var removeComponent = leftArray.Slice(size * fromRow, size);
            var swapComponent = leftArray.Slice(size * (from._count - 1), size);
            removeComponent.CopyTo(insertComponent);
            swapComponent.CopyTo(removeComponent);

            //if (!isLeft)
            //    ++i;
            //else
            //    ++j;
        }
    }

    private static void MakeEdges(Archetype left, Archetype right, int componentID)
    {
        left._edgesRight.Add(new EcsEdge() { Archetype = right, ComponentID = componentID });
        right._edgesLeft.Add(new EcsEdge() { Archetype = left, ComponentID = componentID });
    }

    private void InsertVertex(Archetype newNode)
    {
        var nodeTypeLen = _sign.Count;
        var newTypeLen = newNode._sign.Count;

        if (nodeTypeLen > newTypeLen - 1)
        {
            return;
        }

        if (nodeTypeLen < newTypeLen - 1)
        {
            foreach (ref var edge in CollectionsMarshal.AsSpan(_edgesRight))
            {
                edge.Archetype.InsertVertex(newNode);
            }

            return;
        }

        if (!_sign.IsSuperset(newNode._sign))
        {
            return;
        }

        var i = 0;
        var newNodeTypeLen = newNode._sign.Count;
        for (; i < newNodeTypeLen && _sign[i] == newNode._sign[i]; ++i) { }

        MakeEdges(newNode, this, _sign[i]);
    }

    private void ResizeComponentArray(int capacity)
    {
        for (int i = 0; i < _sign.Count; ++i)
        {
            Array.Resize(ref _components[i], _sizes[_sign[i]] * capacity);
            _capacity = capacity;
        }
    }
}

public struct Query : IQueryComposition
{
    private readonly World _world;
    private readonly EcsSignature _add, _remove;
    private readonly Stack<Archetype> _stack;

    internal Query(World world)
    {
        _world = world;
        _add = new EcsSignature(16);
        _remove = new EcsSignature(16);
        _stack = new Stack<Archetype>();
    }

    public IQueryComposition With<T>() where T : struct
    {
        _add.Add(_world._storage.GetOrCreateID<T>());

        return this;
    }

    public IQueryComposition Without<T>() where T : struct
    {
        _remove.Add(_world._storage.GetOrCreateID<T>());

        return this;
    }

    public IQueryComposition WithTag(int componentID)
    {
        _add.Add(componentID);

        return this;
    }

    public IQueryComposition WithoutTag(int componentID)
    {
        _remove.Add(componentID);

        return this;
    }

    public QueryIterator GetEnumerator()
    {
        _stack.Clear();

        var hash = _add.GetHashCode();
        if (_world._typeIndex.TryGetValue(hash, out var arch))
        {
            var ok = true;
            foreach (var id in _remove)
            {
                if (arch.Signature.IndexOf(id) >= 0)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                _stack.Push(arch);
        }

        return new QueryIterator(_world, _stack, _remove);
    }
}

public interface IQueryComposition
{
    IQueryComposition With<T>() where T : struct;
    IQueryComposition Without<T>() where T : struct;
    IQueryComposition WithTag(int componentID);
    IQueryComposition WithoutTag(int componentID);
    QueryIterator GetEnumerator();
}


[SkipLocalsInit]
public ref struct QueryIterator
{
    private readonly World _world;
    private readonly EcsSignature _remove;
    private readonly Stack<Archetype> _stack;

    private Archetype? _archetype;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryIterator(World world, Stack<Archetype> stack, EcsSignature remove)
    {
        world!.BeginDefer();

        _world = world;
        _remove = remove;

        stack.TryPeek(out _archetype);
        _stack = stack;
    }

    public readonly Iterator Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new Iterator
        (
            _world,
            _archetype!
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        do
        {
            if (_archetype == null || !_stack.TryPop(out _archetype))
                return false;

            for (int i = _archetype._edgesRight.Count - 1; i >= 0; i--)
            {
                if (_remove.IndexOf(_archetype._edgesRight[i].ComponentID) < 0)
                {
                    _stack.Push(_archetype._edgesRight[i].Archetype);
                }
            }
        }
        while (_archetype.Count <= 0);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Dispose()
    {
        _stack.Clear();
        _world!.EndDefer();
        _world!.MergeDeferred();
    }
}


[SkipLocalsInit]
public ref struct Iterator
{
    private readonly World _world;
    private readonly byte[][] _components;
    private readonly int[] _columns;
    private ref EntityID _firstEntity;

    internal Iterator(World world, [NotNull] Archetype archetype)
    {
        _world = world;
        Count = archetype.Count;
        _columns = archetype.Lookup;
        _components = archetype._components;
        _firstEntity = ref MemoryMarshal.GetReference<EntityID>(archetype.Entities);
    }


    public readonly int Count;

    // NOTE: returning Span<T> is a way slower... have I did anything wrong? :\
    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    //public readonly unsafe Span<T> Field<T>() where T : struct
    //{
    //    var componentID = _world._storage.GetID<T>();
    //    var span = _components[_columns[componentID]].AsSpan(0, Count * Unsafe.SizeOf<T>());

    //    // 813
    //    //return new Span<T>(Unsafe.AsPointer<T>(ref Unsafe.As<byte, T>(ref MemoryMarshal.AsRef<byte>(span))), Count);
    //    //return new Span<T>(Unsafe.AsPointer(ref MemoryMarshal.AsRef<T>(span)), Count);

    //    return new Span<T>(Unsafe.AsPointer<T>(ref Unsafe.As<byte, T>(ref span[0])), Count);
    //    //return new Span<T>(Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)), Count);
    //}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref T Field<T>() where T : struct
    {
        var componentID = _world._storage.GetID<T>();
        var span = _components[_columns[componentID]].AsSpan(0, Count * Unsafe.SizeOf<T>());
        return ref MemoryMarshal.AsRef<T>(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : struct
    {
        var componentID = _world._storage.GetID<T>();
        return componentID < _columns.Length && _columns[componentID] >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(ref T first, int row) where T : struct
        => ref Unsafe.Add(ref first, row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref readonly EntityID Entity(int index)
        => ref Unsafe.Add(ref _firstEntity, index);
}

sealed class EcsSignature : IEquatable<EcsSignature>, IDisposable
{
    private int[] _components;
    private int _count, _capacity;

    public EcsSignature(int capacity)
    {
        _capacity = capacity;
        _count = 0;
        _components = capacity <= 0 ? Array.Empty<int>() : new int[capacity];
    }

    public EcsSignature(ReadOnlySpan<int> components)
    {
        _capacity = components.Length;
        _count = components.Length;
        _components = new int[components.Length];
        components.CopyTo(_components);

        Array.Sort(_components, 0, _count);
    }

    public EcsSignature(EcsSignature other)
    {
        _capacity = other._capacity;
        _count = other._count;
        _components = new int[other._components.Length];
        other._components.CopyTo(_components, 0);

        Array.Sort(_components, 0, _count);
    }


    public int Count => _count;
    public ReadOnlySpan<int> Components => _components.AsSpan(0, _count);
    public ref readonly int this[int index] => ref _components[index];



    public void Add(int id)
    {
        GrowIfNeeded();

        _components[_count++] = id;
        Array.Sort(_components, 0, _count);
    }

    public void Remove(int id)
    {
        var idx = IndexOf(id);
        if (idx < 0 || _count <= 0) return;

        _components[idx] = _components[--_count];
        Array.Sort(_components, 0, _count);
    }

    public int IndexOf(int id) => Array.IndexOf(_components, id, 0, _count);

    public bool IsSuperset([NotNull] EcsSignature other)
    {
        int i = 0, j = 0;
        while (i < Count && j < other.Count)
        {
            if (_components[i] == other._components[j])
            {
                j++;
            }

            i++;
        }

        return j == other.Count;
    }

    public bool Equals(EcsSignature? other)
    {
        if (Count != other!.Count)
        {
            return false;
        }

        for (int i = 0, count = Count; i < count; ++i)
        {
            if (_components[i] != other._components[i])
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => ComponentHasher.Calculate(_components.AsSpan(0, _count));

    private void GrowIfNeeded()
    {
        if (_count == _capacity)
        {
            if (_capacity == 0) _capacity = 1;

            _capacity *= 2;
            Array.Resize(ref _components, _capacity);
        }
    }

    public Span<int>.Enumerator GetEnumerator() => _components.AsSpan(0, _count).GetEnumerator();

    public void Dispose()
    {
        if (_components != null)
        {
            _count = 0;
            _components = Array.Empty<int>();
        }
    }
}


public delegate void CallbackIterator(in Iterator iterator);

readonly record struct EcsSystem(IQueryComposition Query, CallbackIterator Func);

readonly record struct EcsEdge(int ComponentID, Archetype Archetype);

record struct EcsRecord(Archetype Archetype, int Row);

static class ComponentHasher
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Calculate(Span<int> components)
    {
        unchecked
        {
            var hash = 5381;

            foreach (ref readonly var id in components)
            {
                hash = ((hash << 5) + hash) + id;
            }

            return hash;
        }
    }
}


//readonly struct EntityID
//{
//    private readonly ulong _value;

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public EntityID(ulong value) => _value = value;
//}


static class IDOp
{
    // ENTITY ID:
    //
    // |----|--|--|
    // | 32 |16|16|
    // | ID |GN|? |

    // COMPONENT ID:
    //
    // |----|--|--|
    // | 32 |16|16|
    // | ID |SZ|? |

    const EntityID ECS_ENTITY_MASK = 0xFFFFFFFFul;
    const EntityID ECS_GENERATION_MASK = (0xFFFFul << 32);
    const EntityID ECS_ID_FLAGS_MASK = (0xFFul << 60);
    const EntityID ECS_COMPONENT_MASK = ~ECS_ID_FLAGS_MASK;

    const EntityID ID_TOGGLE = 1ul << 61;

    public static void Toggle(ref EntityID id)
    {
        //id ^= ID_TOGGLE;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityID GetGeneration(EntityID id)
    {
        return ((id & ECS_GENERATION_MASK) >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncreaseGeneration(ref EntityID id)
    {
        id = ((id & ~ECS_GENERATION_MASK) | ((0xFFFF & (GetGeneration(id) + 1)) << 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityID RealID(EntityID id)
    {
        return id &= ECS_ENTITY_MASK;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasFlag(EntityID id, byte flag)
    {
        return (id & flag) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsComponent(EntityID id)
    {
        return (id & ECS_COMPONENT_MASK) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityID SetAsComponent(EntityID id)
    {
        return id |= ECS_ID_FLAGS_MASK;
    }
}

public sealed partial class World
{
    [SkipLocalsInit]
    public void Set<T>(EntityID entity, T component = default) where T : struct
        => Set(entity, _storage.GetOrCreateID<T>(), MemoryMarshal.AsBytes(new Span<T>(ref component)));

    public int Unset<T>(EntityID entity) where T : struct
       => Detach(entity, _storage.GetOrCreateID<T>());

    public int Tag(EntityID entity, EntityID componentID)
    {
        var id = IDOp.RealID(componentID);

        //IDOp.HasFlag
        //_entities

        //var globalIdx = _storage.GetGlobalID(id);
        //return Attach(entity, globalIdx);

        return (int)id;
    }

    public void UnTag(EntityID entity, int componentID)
        => Detach(entity, componentID);

    public unsafe bool Has<T>(EntityID entity) where T : struct
        => Has(entity, _storage.GetOrCreateID<T>());

    public unsafe ref T Get<T>(EntityID entity) where T : struct
    {
        var raw = Get(entity, _storage.GetOrCreateID<T>());
        return ref MemoryMarshal.AsRef<T>(raw);
    }
}
