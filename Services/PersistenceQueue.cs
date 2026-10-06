namespace SnapAnchor.Services;

/// <summary>Serial background writes preserve event order without blocking the dispatcher.</summary>
internal static class PersistenceQueue
{
    private static readonly object Sync = new();
    private static Task _tail = Task.CompletedTask;

    public static Task<T> Run<T>(Func<T> action)
    {
        lock (Sync)
        {
            var previous = _tail;
            var next = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { /* A failed write must not poison the queue. */ }
                return action();
            });
            _tail = next;
            return next;
        }
    }

    public static Task Run(Action action) => Run(() => { action(); return true; });
    public static async Task FlushAsync()
    {
        while (true)
        {
            Task tail;
            lock (Sync) tail = _tail;
            await tail.ConfigureAwait(false);
            lock (Sync) { if (ReferenceEquals(tail, _tail)) return; }
        }
    }
}
