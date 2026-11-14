namespace Unciv.Utils;

// Port of Kotlin's `by lazy { }` (default mode SYNCHRONIZED, kotlin.SynchronizedLazyImpl).
// System.Lazy<T> is banned (BannedSymbols.txt): in its default mode it caches an exception thrown by the
// factory and rethrows it forever. Kotlin does not: a failed initializer leaves the lazy uninitialized and
// the next access runs it again. Like Kotlin, the factory is released after a successful run, the lock
// is reentrant (recursive access is the caller's bug, as in Kotlin), and a fast volatile check avoids locking once initialized.
public sealed class KotlinLazy<T>
{
    private sealed class Box(T value)
    {
        public readonly T Value = value;
    }

    private readonly object syncRoot = new();
    private Func<T>? initializer;
    private volatile Box? box;

    public KotlinLazy(Func<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        this.initializer = initializer;
    }

    public bool IsValueCreated => box != null;

    public T Value
    {
        get
        {
            if (box is { } initializedBox) return initializedBox.Value;
            lock (syncRoot)
            {
                if (box is { } lockedBox) return lockedBox.Value;
                var init = initializer ?? throw new InvalidOperationException("KotlinLazy initializer missing before initialization.");
                var created = new Box(init()); // an exception propagates and nothing is cached
                box = created;                  // publish (volatile write) after Value is set
                initializer = null;
                return created.Value;
            }
        }
    }

    public override string ToString() => box is { } initializedBox ? initializedBox.Value?.ToString() ?? "" : "Lazy value not initialized yet.";
}
