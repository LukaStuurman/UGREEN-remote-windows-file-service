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
                        window.LastEnd = offset + count;
                        return cached.AsSpan((int)within, count).ToArray();
                    }
                }

                // Keep the first/random small read small. Only a following contiguous
                // read starts read-ahead, avoiding a 1 MiB download for a tiny header.
                fetchLength = window.LastEnd == offset ? Math.Max(length, WindowSize) : length;
                fetchLength = (int)Math.Min(fetchLength, long.MaxValue - offset);
                if (fetchLength <= WindowSize)
                {
                    key = new FetchKey(path, offset, fetchLength, session, window.Generation);
                    flight = _inflight.GetOrAdd(key, _ => new Lazy<byte[]>(
                        () => fetch(path, offset, fetchLength), LazyThreadSafetyMode.ExecutionAndPublication));
                }
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
    }

    private readonly record struct FetchKey(string Path, long Offset, int Length, int Session, long Generation);
}
