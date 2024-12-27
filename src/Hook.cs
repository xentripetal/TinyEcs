namespace TinyEcs;

/// <summary>
/// Optional hooks that can be defined for a component type. This will be called when a component is added, removed, or changed.
/// </summary>
public struct ComponentHooks
{
	public ComponentHooks(Action<World, EcsID, int, Array>? onComponentAdded = null,
		Action<World, EcsID, int, Array>? onComponentUnset = null)
	{
		OnComponentAdded = onComponentAdded;
		OnComponentUnset = onComponentUnset;
	}

	public readonly Action<World, EcsID, int, Array?>? OnComponentAdded;
	public readonly Action<World, EcsID, int, Array?>? OnComponentUnset;
}

/// <summary>
/// Interface for <see cref="ComponentHooks"/>. When a type with this interface is used as a component, these hooks will
/// be registered. Note that the implementation should treat its scope as a static context. The hooks will be called on
/// a default instance of the type. This does not use abstract static interfaces since there's no easy way to interact
/// with them while allowing this interface to be optional.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHookedComponent<T> where T : struct
{
	/// <summary>
	/// Called when a component is set on an entity. Called
	/// </summary>
	/// <param name="world"></param>
	/// <param name="entity"></param>
	/// <param name="value"></param>
	public void OnComponentSet(World world, EcsID entity, ref T value);

	/// <summary>
	/// Called when a component is removed from an entity. Will be called before the component is removed while the world is not locked.
	/// </summary>
	/// <param name="world"></param>
	/// <param name="entity"></param>
	/// <param name="value"></param>
	public void OnComponentUnset(World world, EcsID entity, T value);
}
