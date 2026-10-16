using System.IO;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using DokanNet;

namespace UGREENRemoteDrive;

internal sealed class DriveFileSystem : IDokanOperations
{
    private const int MaxWriteChunkSize = 4 * 1024 * 1024;
    private readonly IFileBackend _backend;
    private readonly DriveMetadataCache _metadata = new(TimeSpan.FromSeconds(1));

    public DriveFileSystem(BackendSelector backends) : this(backends.Current) { }

    internal DriveFileSystem(IFileBackend backend) => _backend = backend;

    private IFileBackend Backend => _backend;
    internal string BackendKind => Backend is EmbeddedTailscaleBackend ? "Ingebouwde Tailscale-SMB" : Backend is SmbBackend smb ? smb.ConnectionName : Backend is RemoteBackend ? "UGREENlink" : "injected";

    private FileEntry Stat(string path) => _metadata.GetStat(path, () => Backend.Stat(path));

    private IReadOnlyList<FileEntry> List(string path) => _metadata.GetList(path, () => Backend.List(path));

    private void InvalidateMetadata() => _metadata.Invalidate();

    public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        var isRoot = IsRootPath(fileName);
        var directoryBefore = info.IsDirectory;
        var entryClass = "unknown";
        NtStatus Finish(NtStatus status)
        {
            if (isRoot)
                DriveLog.Info($"Root callback: backend={BackendKind}; operation=CreateFile; status={status}; entry={entryClass}; mode={mode}; directory-before={directoryBefore}; directory-after={info.IsDirectory}.");
            return status;
        }

        try
        {
            if (Backend is IHandleFileBackend handles)
            {
                FileEntry? existing = null;
                try { existing = Backend.Stat(fileName); }
                catch (DriveApiException ex) when (ex.Status == 404) { }
                if (isRoot && existing is null) return Finish(DokanResult.PathNotFound);
                if (info.IsDirectory && existing is { IsDirectory: false }) return Finish(DokanResult.NotADirectory);
                info.IsDirectory = existing?.IsDirectory ?? info.IsDirectory;
                if (isRoot && mode is not (FileMode.Open or FileMode.OpenOrCreate)) return Finish(DokanResult.AccessDenied);
                var handle = handles.OpenFile(fileName, access, share, mode, options, attributes, info.IsDirectory);
                try { info.IsDirectory = handle.Stat().IsDirectory; }
                catch { handle.Dispose(); throw; }
                info.Context = handle;
                InvalidateMetadata();
                return Finish(handle.Existed && mode is (FileMode.Create or FileMode.OpenOrCreate) ? DokanResult.AlreadyExists : DokanResult.Success);
            }
            FileEntry? entry = null;
            try
            {
                entry = Stat(fileName);
                entryClass = EntryClass(entry);
            }
            catch (DriveApiException ex) when (ex.Status == 404) { entryClass = "missing"; }
            catch (FileNotFoundException) { entryClass = "missing"; }
            catch (DirectoryNotFoundException) { entryClass = "missing"; }

            // A failed root lookup must never turn an Explorer open into a remote create.
            if (isRoot && entry is null) return Finish(DokanResult.PathNotFound);

            // Dokan requires the filesystem to set IsDirectory when metadata proves
            // that the target is a folder; the incoming flag alone is not sufficient.
            if (entry is { IsDirectory: true })
            {
                info.IsDirectory = true;
                return Finish(mode == FileMode.CreateNew ? DokanResult.AlreadyExists : DokanResult.Success);
            }

            if (info.IsDirectory)
            {
                if (entry is { IsDirectory: false }) return Finish(DokanResult.NotADirectory);
                if (entry is null)
                {
                    if (mode is not (FileMode.CreateNew or FileMode.Create or FileMode.OpenOrCreate)) return Finish(DokanResult.PathNotFound);
                    Backend.CreateDirectory(fileName);
                    InvalidateMetadata();
                    entryClass = "directory-created";
                    return Finish(DokanResult.Success);
                }
                return Finish(mode == FileMode.CreateNew ? DokanResult.AlreadyExists : DokanResult.Success);
            }

            switch (mode)
            {
                case FileMode.Open:
                    return Finish(entry is null ? DokanResult.FileNotFound : DokanResult.Success);
                case FileMode.CreateNew:
                    if (entry is not null) return Finish(DokanResult.FileExists);
                    Backend.CreateFile(fileName);
                    InvalidateMetadata();
                    return Finish(DokanResult.Success);
                case FileMode.Create:
                    if (entry is null) Backend.CreateFile(fileName);
                    else Backend.Resize(fileName, 0);
                    InvalidateMetadata();
                    return Finish(DokanResult.Success);
                case FileMode.OpenOrCreate:
                    if (entry is null)
                    {
                        Backend.CreateFile(fileName);
                        InvalidateMetadata();
                    }
                    return Finish(entry is null ? DokanResult.Success : DokanResult.AlreadyExists);
                case FileMode.Truncate:
                    if (entry is null) return Finish(DokanResult.FileNotFound);
                    Backend.Resize(fileName, 0);
                    InvalidateMetadata();
                    return Finish(DokanResult.Success);
                case FileMode.Append:
                    if (entry is null)
                    {
                        Backend.CreateFile(fileName);
                        InvalidateMetadata();
                    }
                    return Finish(DokanResult.Success);
                default:
                    return Finish(DokanResult.InvalidParameter);
            }
        }
        catch (Exception ex)
        {
            entryClass = "error-" + ex.GetType().Name;
            return Finish(Map(ex, nameof(CreateFile)));
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (Backend is IHandleFileBackend)
        {
            // Keep the file ID alive until CloseFile: cached/paging I/O may
            // still arrive after Cleanup. Delete by ID, never by reused path.
            if (info.DeletePending && info.Context is IBackendFileHandle handle)
                try { handle.Delete(); InvalidateMetadata(); }
                catch (Exception ex) { DriveLog.Error($"Handle cleanup failed ({ex.GetType().Name})."); }
            return;
        }
        if (!info.DeletePending) return;
        try
        {
            Backend.Delete(fileName, info.IsDirectory);
            InvalidateMetadata();
        }
        catch (Exception ex) { DriveLog.Error($"Cleanup failed ({ex.GetType().Name})."); }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is not IBackendFileHandle handle) return;
        try { handle.Dispose(); }
        catch (Exception ex) { DriveLog.Error($"Handle close failed ({ex.GetType().Name})."); }
        finally { info.Context = null; }
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        try
        {
            if (offset < 0) return DokanResult.InvalidParameter;
            var data = Backend is IHandleFileBackend
                ? Handle(info).Read(offset, Math.Min(buffer.Length, 4 * 1024 * 1024))
                : Backend.Read(fileName, offset, Math.Min(buffer.Length, 4 * 1024 * 1024));
            data.CopyTo(buffer, 0);
            bytesRead = data.Length;
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(ReadFile)); }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        try
        {
            var actualOffset = offset == -1 || info.WriteToEndOfFile ? -1 : offset;
            if (actualOffset < -1) return DokanResult.InvalidParameter;
            var handle = Backend is IHandleFileBackend ? Handle(info) : null;
            var writeLength = buffer.Length;
            if (handle is not null && info.PagingIo)
            {
                var size = handle.Stat().Size;
                if (actualOffset < 0) actualOffset = size;
                if (actualOffset >= size) return DokanResult.Success;
                writeLength = (int)Math.Min(writeLength, size - actualOffset);
            }
            var written = 0;
            while (written < writeLength)
            {
                // The server accepts up to 4 MiB per request. Using the full limit
                // avoids extra remote round trips and repeated SMB file opens.
                var count = Math.Min(MaxWriteChunkSize, writeLength - written);
                var chunk = written == 0 && count == buffer.Length ? buffer : buffer.AsSpan(written, count).ToArray();
                var at = actualOffset < 0 ? -1 : actualOffset + written;
                var current = handle is null ? Backend.Write(fileName, at, chunk) : handle.Write(at, chunk);
                if (current > chunk.Length) throw new IOException("Ongeldige schrijflengte.");
                if (current <= 0) break;
                written += current;
                InvalidateMetadata();
                if (current < chunk.Length) break;
            }
            bytesWritten = written;
            return written == writeLength ? DokanResult.Success : DokanResult.Error;
        }
        catch (Exception ex) { return Map(ex, nameof(WriteFile)); }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (Backend is not IHandleFileBackend) return DokanResult.Success;
        try { Handle(info).Flush(); return DokanResult.Success; }
        catch (Exception ex) { return Map(ex, nameof(FlushFileBuffers)); }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var isRoot = IsRootPath(fileName);
        try
        {
            var entry = Backend is IHandleFileBackend && info.Context is IBackendFileHandle handle ? handle.Stat() : Stat(fileName);
            fileInfo = ToDokan(entry, fileName);
            if (isRoot)
                DriveLog.Info($"Root callback: backend={BackendKind}; operation=GetFileInformation; status={DokanResult.Success}; entry={EntryClass(entry)}; empty-name={string.IsNullOrEmpty(entry.Name)}.");
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            fileInfo = default;
            var status = Map(ex, nameof(GetFileInformation));
            if (isRoot)
                DriveLog.Info($"Root callback: backend={BackendKind}; operation=GetFileInformation; status={status}; failure={ex.GetType().Name}.");
            return status;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var isRoot = IsRootPath(fileName);
        try
        {
            var entries = List(fileName);
            files = entries.Select(entry => ToDokan(entry, entry.Name)).ToArray();
            if (isRoot)
            {
                var directories = entries.Count(entry => entry.IsDirectory);
                var invalidNames = entries.Count(entry => !IsValidWindowsComponentName(entry.Name));
                DriveLog.Info($"Root callback: backend={BackendKind}; operation=FindFiles; status={DokanResult.Success}; entries={entries.Count}; directories={directories}; invalid-names={invalidNames}.");
            }
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            files = Array.Empty<FileInformation>();
            var status = Map(ex, nameof(FindFiles));
            if (isRoot)
                DriveLog.Info($"Root callback: backend={BackendKind}; operation=FindFiles; status={status}; failure={ex.GetType().Name}.");
            return status;
        }
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var status = FindFiles(fileName, out var all, info);
        if (status != DokanResult.Success)
        {
            files = Array.Empty<FileInformation>();
            return status;
        }
        try
        {
            var regex = "^" + Regex.Escape(searchPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            files = all.Where(item => Regex.IsMatch(item.FileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            files = Array.Empty<FileInformation>();
            return Map(ex, nameof(FindFilesWithPattern));
        }
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        if (Backend is not IHandleFileBackend) return DokanResult.NotImplemented;
        try { Handle(info).SetAttributes(attributes); InvalidateMetadata(); return DokanResult.Success; }
        catch (Exception ex) { return Map(ex, nameof(SetFileAttributes)); }
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        if (lastWriteTime is null) return DokanResult.Success;
        try
        {
            var modified = new DateTimeOffset(lastWriteTime.Value.ToUniversalTime());
            if (Backend is IHandleFileBackend) Handle(info).SetModifiedTime(modified);
            else Backend.SetModifiedTime(fileName, modified);
            InvalidateMetadata();
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(SetFileTime)); }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (Backend is IHandleFileBackend)
            {
                var handle = Handle(info);
                if (handle.Stat().IsDirectory) return DokanResult.NotADirectory;
                handle.Delete(); info.DeletePending = true; InvalidateMetadata();
                return DokanResult.Success;
            }
            var entry = Stat(fileName);
            if (entry.IsDirectory) return DokanResult.NotADirectory;
            info.DeletePending = true;
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(DeleteFile)); }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        try
        {
            if (Backend is IHandleFileBackend)
            {
                var handle = Handle(info);
                if (!handle.Stat().IsDirectory) return DokanResult.NotADirectory;
                handle.Delete(); info.DeletePending = true; InvalidateMetadata();
                return DokanResult.Success;
            }
            var entry = Stat(fileName);
            if (!entry.IsDirectory) return DokanResult.NotADirectory;
            // Emptiness is a safety decision: do not use the short-lived Explorer cache.
            if (Backend.List(fileName).Count != 0) return DokanResult.DirectoryNotEmpty;
            info.DeletePending = true;
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(DeleteDirectory)); }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        try
        {
            if (Backend is IHandleFileBackend) Handle(info).Rename(newName, replace);
            else Backend.Rename(oldName, newName, replace);
            InvalidateMetadata();
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(MoveFile)); }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        try
        {
            if (length < 0) return DokanResult.InvalidParameter;
            if (Backend is IHandleFileBackend) Handle(info).Resize(length);
            else Backend.Resize(fileName, length);
            InvalidateMetadata();
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(SetEndOfFile)); }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        if (Backend is not IHandleFileBackend) return DokanResult.Success;
        try
        {
            if (length < 0) return DokanResult.InvalidParameter;
            Handle(info).Reserve(length); InvalidateMetadata();
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(SetAllocationSize)); }
    }

    private static IBackendFileHandle Handle(IDokanFileInfo info) =>
        info.Context as IBackendFileHandle ?? throw new IOException("De geopende bestandsreferentie ontbreekt.");

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        try
        {
            var space = Backend.GetSpace();
            freeBytesAvailable = space.Available;
            totalNumberOfBytes = space.Total;
            totalNumberOfFreeBytes = space.Free;
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            freeBytesAvailable = totalNumberOfBytes = totalNumberOfFreeBytes = 0;
            return Map(ex, nameof(GetDiskFreeSpace));
        }
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName,
        out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "UGREEN NAS";
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk | FileSystemFeatures.SupportsRemoteStorage;
        fileSystemName = "UGREEN-Drive";
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        => DokanResult.NotImplemented;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        DriveLog.Info("Drive mounted.");
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        DriveLog.Info("Drive unmounted.");
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    private static FileInformation ToDokan(FileEntry entry, string name) => new()
    {
        FileName = name,
        Attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Archive,
        CreationTime = entry.CreatedUtc.UtcDateTime,
        LastWriteTime = entry.ModifiedUtc.UtcDateTime,
        Length = entry.IsDirectory ? 0 : entry.Size
    };

    private static bool IsRootPath(string fileName) =>
        string.IsNullOrEmpty(fileName) || fileName.All(character => character is '\\' or '/');

    private static string EntryClass(FileEntry entry) => entry.IsDirectory ? "directory" : "file";

    private static bool IsValidWindowsComponentName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." ||
            name.EndsWith(' ') || name.EndsWith('.')) return false;
        if (name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character))) return false;

        var baseName = name.Split('.')[0].TrimEnd(' ', '.');
        var upperBaseName = baseName.ToUpperInvariant();
        return upperBaseName is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !Regex.IsMatch(upperBaseName, @"^(COM|LPT)[1-9]$");
    }

    private static NtStatus Map(Exception exception, string operation)
    {
        DriveLog.Error($"{operation} failed ({exception.GetType().Name}).");
        return exception switch
        {
            DriveApiException api when api.Status == 401 || api.Status == 403 => DokanResult.AccessDenied,
            DriveApiException api when api.Status == 404 => DokanResult.FileNotFound,
            DriveApiException api when api.Status == 409 && api.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => DokanResult.DirectoryNotEmpty,
            DriveApiException api when api.Status == 409 => DokanResult.AlreadyExists,
            DriveApiException api when api.Status == 400 => DokanResult.InvalidParameter,
            DriveApiException api when api.Status == 423 => DokanResult.SharingViolation,
            DriveApiException api when api.Status == 422 => DokanResult.DirectoryNotEmpty,
            DriveApiException => DokanResult.Error,
            DriveDisconnectedException => DokanResult.NotReady,
            FileNotFoundException => DokanResult.FileNotFound,
            DirectoryNotFoundException => DokanResult.PathNotFound,
            UnauthorizedAccessException => DokanResult.AccessDenied,
            IOException io when io.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => DokanResult.DirectoryNotEmpty,
            ArgumentException => DokanResult.InvalidParameter,
            _ => DokanResult.Error
        };
    }
}
