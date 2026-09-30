using NovaGet.Core.Ipc;

namespace NovaGet.Core.Tests.Ipc;

public sealed class SingleInstanceGuardTests
{
    // Mutex ownership is per thread, so each "instance" runs on its own dedicated thread.
    private static T OnNewThread<T>(Func<T> func)
    {
        T result = default!;
        var thread = new Thread(() => result = func());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void Second_instance_is_refused_until_first_releases()
    {
        var name = "NovaGet.Test." + Guid.NewGuid().ToString("N")[..12];
        using var release = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        var firstGotIt = false;

        var first = new Thread(() =>
        {
            using var guard = SingleInstanceGuard.TryAcquire(name);
            firstGotIt = guard is not null;
            acquired.Set();
            release.Wait();
        });
        first.Start();
        acquired.Wait();

        var second = OnNewThread(() => SingleInstanceGuard.TryAcquire(name));
        Assert.True(firstGotIt);
        Assert.Null(second);

        release.Set();
        first.Join();

        var third = OnNewThread(() =>
        {
            var guard = SingleInstanceGuard.TryAcquire(name);
            var ok = guard is not null;
            guard?.Dispose();
            return ok;
        });
        Assert.True(third);
    }
}
