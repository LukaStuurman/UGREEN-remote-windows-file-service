using System.Collections.Concurrent;

namespace UGREENRemoteDrive;

/// <summary>Small, memory-only read windows for bursts of sequential remote reads.</summary>
internal sealed class RemoteReadAheadCache(Func<long>? clock = null)
{
    internal const int WindowSize = 1024 * 1024;
    private const int MaximumFiles = 8;
    private const int LifetimeMilliseconds = 1000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<FetchKey, Lazy<byte[]>> _inflight = new();
    private readonly SemaphoreSlim _prefetchSlots = new(2, 2);
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _generation;

    internal byte[] Read(string path, long offset, int length, int session,
        Func<string, long, int, byte[]> fetch, Func<bool> sessionValid)
    {
        if (offset < 0 || length < 0 || length > 4 * WindowSize || offset > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!sessionValid()) throw new DriveDisconnectedException("De remote aanmelding is niet meer geldig.");
        if (length == 0) return [];
        while (true)
        {
            var window = GetWindow(path, session);
            int fetchLength;
            FetchKey key = default;
            Lazy<byte[]>? flight = null;
            Prefetch? waiting = null;
            lock (window.Gate)
            {
                if (!IsCurrent(path, window)) continue;
                if (!sessionValid()) throw new DriveDisconnectedException("De remote aanmelding is niet meer geldig.");
                var now = _clock();
                if (now >= window.ExpiresAt)
                {
                    window.Data = null;
                    window.LastEnd = -1;
                }
                window.LastUsed = now;
                if (window.Data is { } cached && offset >= window.Offset)
                {
                    var within = offset - window.Offset;
                    if (within <= cached.Length &&
                        (within + length <= cached.Length || window.EndOfFile))
                    {
                        if (!sessionValid()) throw new DriveDisconnectedException("De remote aanmelding is niet meer geldig.");
                        var count = (int)Math.Min(length, cached.Length - within);
                        var sequential = window.LastEnd == offset;
                        window.LastEnd = offset + count;
                        if (sequential && !window.EndOfFile && cached.Length == WindowSize &&
                            within + count >= WindowSize / 2)
                            StartPrefetch(path, window, fetch, sessionValid);
                        return cached.AsSpan((int)within, count).ToArray();
                    }
                }

                // Keep the first/random small read small. Only a following contiguous
                // read starts read-ahead, avoiding a 1 MiB download for a tiny header.
                fetchLength = window.LastEnd == offset ? Math.Max(length, WindowSize) : length;
                fetchLength = (int)Math.Min(fetchLength, long.MaxValue - offset);
                if (window.Next is { } next && offset >= next.Offset &&
                    offset - next.Offset + length <= WindowSize)
                    waiting = next;
                if (waiting is null && fetchLength <= WindowSize)
                {
                    key = new FetchKey(path, offset, fetchLength, session, window.Generation);
                    flight = _inflight.GetOrAdd(key, _ => new Lazy<byte[]>(
                        () => fetch(path, offset, fetchLength), LazyThreadSafetyMode.ExecutionAndPublication));
                }
            }
            if (waiting is not null)
            {
                var ready = waiting.Work.GetAwaiter().GetResult();
                if (!sessionValid()) throw new DriveDisconnectedException("De remote aanmelding is gewijzigd tijdens het lezen.");
                lock (window.Gate)
                {
                    if (!IsCurrent(path, window)) continue;
                    if (ReferenceEquals(window.Next, waiting)) window.Next = null;
                    if (ready is not null && _clock() < ready.ExpiresAt)
                    {
                        window.Offset = waiting.Offset;
                        window.Data = ready.Data;
                        window.EndOfFile = ready.Data.Length < WindowSize;
                        window.ExpiresAt = ready.ExpiresAt;
                        continue;
                    }
                }
                // Failed/expired speculation is retried only as a normal read.
                // Return to the lookup so duplicate demand reads still coalesce.
                continue;
            }
            // Only identical requests share a flight. Different offsets must not
            // wait behind each other: Windows copies can have parallel reads.
            byte[] data;
            try
            {
                data = flight is not null ? flight.Value : fetch(path, offset, fetchLength);
                if (!sessionValid()) throw new DriveDisconnectedException("De remote aanmelding is gewijzigd tijdens het lezen.");
                if (data.Length > fetchLength) throw new IOException("Ongeldige lengte van het remote leesantwoord.");
                var returned = Math.Min(length, data.Length);
                lock (window.Gate)
                {
                    if (IsCurrent(path, window))
                    {
                        window.Offset = offset;
                        window.Data = data.Length <= WindowSize ? data : null;
                        window.Next = null;
                        window.EndOfFile = data.Length < fetchLength;
                        window.LastEnd = offset + returned;
                        window.ExpiresAt = _clock() + LifetimeMilliseconds;
                    }
                }
                // Publish the window before removing the shared flight. Small
                // retained arrays must not be exposed to callers for mutation.
                return returned == data.Length && data.Length > WindowSize
                    ? data : data.AsSpan(0, returned).ToArray();
            }
            catch
            {
                lock (window.Gate)
                {
                    window.Data = null;
                    window.LastEnd = -1;
                }
                throw;
            }
            finally
            {
                if (flight is not null)
                    ((ICollection<KeyValuePair<FetchKey, Lazy<byte[]>>>)_inflight).Remove(new(key, flight));
            }
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            unchecked { _generation++; }
            _windows.Clear();
        }
    }

    private void StartPrefetch(string path, Window window, Func<string, long, int, byte[]> fetch,
        Func<bool> sessionValid)
    {
        // Called only while consuming the second half of a full sequential
        // window. Keep one next window per file and at most two network workers.
        if (window.Next is not null || window.Offset > long.MaxValue - 2L * WindowSize ||
            !sessionValid() || !_prefetchSlots.Wait(0)) return;
        var offset = window.Offset + WindowSize;
        window.Next = new Prefetch(offset, Task.Run(() =>
        {
            try
            {
                if (!sessionValid() || !IsCurrent(path, window)) return null;
                var data = fetch(path, offset, WindowSize);
                if (data.Length > WindowSize || !sessionValid() || !IsCurrent(path, window)) return null;
                return new PrefetchedData(data, _clock() + LifetimeMilliseconds);
            }
            catch { return null; } // Speculation never reports success for a failed demand read.
            finally { _prefetchSlots.Release(); }
        }));
    }

    private Window GetWindow(string path, int session)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(path, out var existing) && existing.Session == session) return existing;
            if (_windows.Count >= MaximumFiles)
            {
                var oldest = _windows.MinBy(pair => pair.Value.LastUsed).Key;
                _windows.Remove(oldest);
            }
            var window = new Window { Generation = _generation, Session = session, LastUsed = _clock() };
            _windows[path] = window;
            return window;
        }
    }

    private bool IsCurrent(string path, Window window)
    {
        lock (_gate) return window.Generation == _generation &&
            _windows.TryGetValue(path, out var current) && ReferenceEquals(window, current);
    }

    private sealed class Window
    {
        internal readonly object Gate = new();
        internal long Generation;
        internal int Session;
        internal long LastUsed;
        internal long LastEnd = -1;
        internal long Offset;
        internal long ExpiresAt;
        internal byte[]? Data;
        internal bool EndOfFile;
        internal Prefetch? Next;
    }

    private sealed record PrefetchedData(byte[] Data, long ExpiresAt);
    private sealed record Prefetch(long Offset, Task<PrefetchedData?> Work);
    private readonly record struct FetchKey(string Path, long Offset, int Length, int Session, long Generation);
}
