namespace Unciv.GodotClient.Ui;

/// <summary>
/// Typed holder for a Godot node or service assigned in <c>_Ready</c> or <c>BuildFrame</c>,
/// throwing <see cref="InvalidOperationException"/> if accessed before initialization.
/// </summary>
public struct ReadyRef<T> where T : class
{
    private T? value;

    public readonly bool IsSet => value != null;

    public readonly T Value => value ?? throw new InvalidOperationException($"{typeof(T).Name} accessed before initialization.");

    public readonly T? OrNull => value;

    public void Set(T assigned)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        value = assigned;
    }

    public static implicit operator T(ReadyRef<T> reference) => reference.Value;
}
