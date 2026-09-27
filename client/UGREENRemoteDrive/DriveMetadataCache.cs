namespace UGREENRemoteDrive;

/// <summary>
/// Keeps the short bursts of repeated Explorer metadata lookups from making a
/// separate network request for every callback. Mutations invalidate the cache;
/// entries learned from an old in-flight request cannot repopulate it afterward.
/// </summary>
internal sealed class DriveMetadataCache(TimeSpan lifetime)
{
    private const int MaximumEntries = 2048;
    private const int MaximumChildrenToSeed = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _lifetimeMilliseconds = Math.Max(1, (long)lifetime.TotalMilliseconds);
    private long _generation;

    internal long Generation
    {
        get { lock (_gate) return _generation; }
    }

    internal bool TryGetStat(string path, out FileEntry entry)
    {
        lock (_gate)
        {
            if (TryGetEntry(Normalize(path), out var cached) && cached.Stat is { } stat)
            {
                entry = stat;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    internal bool TryGetList(string path, out IReadOnlyList<FileEntry> entries)
    {
        lock (_gate)
        {
            if (TryGetEntry(Normalize(path), out var cached) && cached.List is { } list)
            {
                entries = list;
                return true;
            }
        }

        entries = Array.Empty<FileEntry>();
        return false;
    }

    internal void StoreStat(string path, FileEntry entry, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            var key = Normalize(path);
            EnsureCapacity(1);
            var cached = GetOrCreate(key);
            cached.Stat = entry;
            Refresh(cached);
        }
    }

    internal void StoreList(string path, IReadOnlyList<FileEntry> entries, long generation)
    {
        var snapshot = entries.ToArray();
        var parent = Normalize(path);
        var seedChildren = snapshot.Length <= MaximumChildrenToSeed;

        lock (_gate)
        {
            if (generation != _generation) return;
            EnsureCapacity(seedChildren ? snapshot.Length + 1 : 1);
            var cached = GetOrCreate(parent);
            cached.List = snapshot;
            Refresh(cached);

            if (!seedChildren) return;
            foreach (var child in snapshot)
            {
                if (string.IsNullOrEmpty(child.Name) || child.Name.Contains('/') || child.Name.Contains('\\')) continue;
                var key = parent.Length == 0 ? child.Name : parent + "\\" + child.Name;
                var childEntry = GetOrCreate(key);
                childEntry.Stat = child;
                Refresh(childEntry);
            }
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            unchecked { _generation++; }
            _entries.Clear();
        }
    }

    private bool TryGetEntry(string key, out CacheEntry cached)
    {
        if (_entries.TryGetValue(key, out cached!) && cached.Generation == _generation &&
            Environment.TickCount64 < cached.ExpiresAt)
            return true;

        _entries.Remove(key);
        cached = null!;
        return false;
    }

    private void EnsureCapacity(int additionalEntries)
    {
        if (_entries.Count + additionalEntries > MaximumEntries)
            _entries.Clear();
    }

    private CacheEntry GetOrCreate(string key)
    {
        if (!_entries.TryGetValue(key, out var cached) || cached.Generation != _generation)
        {
            cached = new CacheEntry { Generation = _generation };
            _entries[key] = cached;
        }
        return cached;
    }

    private void Refresh(CacheEntry cached)
    {
        cached.Generation = _generation;
        cached.ExpiresAt = Environment.TickCount64 + _lifetimeMilliseconds;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').Trim('\\');

    private sealed class CacheEntry
    {
        public FileEntry? Stat { get; set; }
        public IReadOnlyList<FileEntry>? List { get; set; }
        public long ExpiresAt { get; set; }
        public long Generation { get; set; }
    }
}
