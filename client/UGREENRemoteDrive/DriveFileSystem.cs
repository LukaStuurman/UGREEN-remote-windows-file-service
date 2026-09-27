using System.IO;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using DokanNet;

namespace UGREENRemoteDrive;

internal sealed class DriveFileSystem : IDokanOperations
{
    private readonly IFileBackend _backend;

    public DriveFileSystem(BackendSelector backends) : this(backends.Current) { }

    internal DriveFileSystem(IFileBackend backend) => _backend = backend;

    private IFileBackend Backend => _backend;
    internal string BackendKind => Backend is SmbBackend ? "LAN-SMB" : Backend is RemoteBackend ? "UGREENlink" : "injected";

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
            FileEntry? entry = null;
            try
            {
                entry = Backend.Stat(fileName);
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
                    return Finish(DokanResult.Success);
                case FileMode.Create:
                    if (entry is null) Backend.CreateFile(fileName);
                    else Backend.Resize(fileName, 0);
                    return Finish(DokanResult.Success);
                case FileMode.OpenOrCreate:
                    if (entry is null) Backend.CreateFile(fileName);
                    return Finish(entry is null ? DokanResult.Success : DokanResult.AlreadyExists);
                case FileMode.Truncate:
                    if (entry is null) return Finish(DokanResult.FileNotFound);
                    Backend.Resize(fileName, 0);
                    return Finish(DokanResult.Success);
                case FileMode.Append:
                    if (entry is null) Backend.CreateFile(fileName);
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
        if (!info.DeletePending) return;
        try { Backend.Delete(fileName, info.IsDirectory); }
        catch (Exception ex) { DriveLog.Error($"Cleanup failed ({ex.GetType().Name})."); }
    }

    public void CloseFile(string fileName, IDokanFileInfo info) { }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        try
        {
            if (offset < 0) return DokanResult.InvalidParameter;
            var data = Backend.Read(fileName, offset, Math.Min(buffer.Length, 4 * 1024 * 1024));
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
            var written = 0;
            while (written < buffer.Length)
            {
                var count = Math.Min(1024 * 1024, buffer.Length - written);
                var chunk = written == 0 && count == buffer.Length ? buffer : buffer.AsSpan(written, count).ToArray();
                var at = actualOffset < 0 ? -1 : actualOffset + written;
                var current = Backend.Write(fileName, at, chunk);
                if (current <= 0) break;
                written += current;
                if (current < chunk.Length) break;
            }
            bytesWritten = written;
            return written == buffer.Length ? DokanResult.Success : DokanResult.Error;
        }
        catch (Exception ex) { return Map(ex, nameof(WriteFile)); }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var isRoot = IsRootPath(fileName);
        try
        {
            var entry = Backend.Stat(fileName);
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
            var entries = Backend.List(fileName);
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

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) => DokanResult.NotImplemented;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        if (lastWriteTime is null) return DokanResult.Success;
        try
        {
            Backend.SetModifiedTime(fileName, new DateTimeOffset(lastWriteTime.Value.ToUniversalTime()));
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(SetFileTime)); }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        try
        {
            var entry = Backend.Stat(fileName);
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
            var entry = Backend.Stat(fileName);
            if (!entry.IsDirectory) return DokanResult.NotADirectory;
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
            Backend.Rename(oldName, newName, replace);
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(MoveFile)); }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        try
        {
            if (length < 0) return DokanResult.InvalidParameter;
            Backend.Resize(fileName, length);
            return DokanResult.Success;
        }
        catch (Exception ex) { return Map(ex, nameof(SetEndOfFile)); }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => DokanResult.Success;

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
