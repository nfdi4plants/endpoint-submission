using System.Collections.Concurrent;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// Serializes async work per string key and drops an entry once nobody holds or waits for it.
/// The lock is per process, and the deployment runs one service instance.
/// </summary>
public sealed class KeyedAsyncLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
        public bool Removed;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Waits for the key and returns a handle that releases it when disposed.</summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var entry = Enter(key);
        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Exit(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private Entry Enter(string key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                // The last holder may have removed this entry between GetOrAdd and the lock.
                if (entry.Removed) continue;
                entry.References++;
                return entry;
            }
        }
    }

    private void Exit(string key, Entry entry)
    {
        lock (entry)
        {
            if (--entry.References > 0) return;
            entry.Removed = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Semaphore.Release();
            owner.Exit(key, entry);
        }
    }
}
