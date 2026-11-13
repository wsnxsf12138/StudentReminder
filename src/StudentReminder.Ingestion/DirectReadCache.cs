namespace StudentReminder.Ingestion;

public enum CacheRemoval { Absent, Removed, UnsafePath, Unavailable }

public readonly record struct CacheRemovalResult(CacheRemoval Wechat, CacheRemoval Qq);

/// <summary>The two app-managed cache trees for one data directory.</summary>
public sealed class DirectReadCache(string dataDirectory)
{
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentReminder");

    public string DataDirectory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
    public string WechatDirectory => Path.Combine(DataDirectory, "wxlocal");
    public string QqDirectory => Path.Combine(DataDirectory, "qqlocal");

    public bool IsInKnownSyncFolder()
    {
        string[] variables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
        foreach (var variable in variables)
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } path ||
                !Path.IsPathFullyQualified(path)) continue;
            var syncRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (DataDirectory.Equals(syncRoot, StringComparison.OrdinalIgnoreCase) ||
                DataDirectory.StartsWith(Path.EndsInDirectorySeparator(syncRoot)
                    ? syncRoot : syncRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return DataDirectory.Split(Path.DirectorySeparatorChar).Any(part =>
            part.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("iCloudDrive", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The caller keeps new readers blocked until this operation completes.</summary>
    public async Task<CacheRemovalResult> ClearAfterPauseAsync(Task stopped)
    {
        await stopped.ConfigureAwait(false);
        return await Task.Run(() => new CacheRemovalResult(Remove(WechatDirectory), Remove(QqDirectory)))
            .ConfigureAwait(false);
    }

    private static CacheRemoval Remove(string path)
    {
        try
        {
            var pending = path + ".clearing";
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (ReadAttributes(parent) is { } attributes && !IsPlainDirectory(attributes))
                    return CacheRemoval.UnsafePath;
            }

            var active = ReadAttributes(path);
            var leftover = ReadAttributes(pending);
            if (active is null && leftover is null) return CacheRemoval.Absent;
            if ((active is { } source && (!IsPlainDirectory(source) || ContainsUnsafeEntry(path))) ||
                (leftover is { } old && (!IsPlainDirectory(old) || ContainsUnsafeEntry(pending))))
                return CacheRemoval.UnsafePath;

            if (leftover is not null) DeleteDetachedCache(pending, path);
            if (active is not null)
            {
                var exports = Path.Combine(path, "exports");
                if (Directory.Exists(exports) && Directory.EnumerateFileSystemEntries(path)
                    .All(entry => entry.Equals(exports, StringComparison.OrdinalIgnoreCase)))
                    return leftover is null ? CacheRemoval.Absent : CacheRemoval.Removed;
                // Detach before deleting so an interrupted removal cannot leave a partially usable cache.
                Directory.Move(path, pending);
                DeleteDetachedCache(pending, path);
            }
            return CacheRemoval.Removed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return CacheRemoval.Unavailable;
        }
    }

    private static void DeleteDetachedCache(string pending, string active)
    {
        // The legacy probes defaulted to these cache roots and stored independent exports there.
        var exports = Path.Combine(pending, "exports");
        if (Directory.Exists(exports))
        {
            Directory.CreateDirectory(active);
            Directory.Move(exports, Path.Combine(active, "exports"));
        }
        Directory.Delete(pending, recursive: true);
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool IsPlainDirectory(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == FileAttributes.Directory;

    private static bool ContainsUnsafeEntry(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    Path.GetFileName(entry).Equals("messages.db", StringComparison.OrdinalIgnoreCase)) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }
}
