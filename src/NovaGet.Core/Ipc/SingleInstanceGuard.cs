namespace NovaGet.Core.Ipc;

/// <summary>
/// Owns the named mutex that marks the running instance. Dispose it on the same thread that
/// acquired it (the UI thread), because Win32 mutexes are thread-affine.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns a guard if this is the first instance, or null if another instance owns the mutex.</summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        try
        {
            // The mutex exists; we only own it if the previous owner is gone.
            if (mutex.WaitOne(0))
            {
                return new SingleInstanceGuard(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing; we now own it.
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on a different thread or already released; nothing else to do.
        }

        _mutex.Dispose();
    }
}
