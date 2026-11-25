using System.Text.Json;
using System.Text.RegularExpressions;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Server;
using SwiftBeam.Core.Settings;

namespace SwiftBeam.Core.Sync;

public sealed record SyncFileRecord(long Size, DateTime LastWriteTimeUtc);

public sealed class AutoSyncSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Dictionary<string, SyncFileRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSyncedUtc { get; set; }

    public static AutoSyncSnapshot Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<AutoSyncSnapshot>(json, JsonOptions);
                if (raw is not null)
                {
                    return new AutoSyncSnapshot
                    {
                        Files = new Dictionary<string, SyncFileRecord>(raw.Files ?? [], StringComparer.OrdinalIgnoreCase),
                        Directories = new HashSet<string>(raw.Directories ?? [], StringComparer.OrdinalIgnoreCase),
                        LastSyncedUtc = raw.LastSyncedUtc
                    };
                }
            }
        }
        catch
        {
            // Treat corrupt snapshot as fresh pairing
        }
        return new AutoSyncSnapshot();
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

public enum AutoSyncActionKind
{
    UploadFile,
    DownloadFile,
    DeleteLocalFile,
    DeleteRemoteFile,
    ConflictKeepBothDownloadRemote,
    CreateLocalDir,
    CreateRemoteDir,
    DeleteLocalDir,
    DeleteRemoteDir
}

public sealed record AutoSyncAction(
    AutoSyncActionKind Kind,
    string RelativePath,
    string LocalFullPath,
    string RemoteFullPath,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    string Reason);

public sealed record AutoSyncPlan(List<AutoSyncAction> Actions, int IdenticalCount);

public static class AutoSyncEngine
{
    private const double TimestampToleranceSeconds = 2.0;

    public static IReadOnlyList<string> ParseIgnorePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        return raw.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(p => p.Length > 0)
                  .ToArray();
    }

    public static bool IsIgnored(string relPath, IReadOnlyList<string> patterns)
    {
        string norm = (relPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(norm) || norm == ".")
            return true;

        string[] segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg.Equals(".swiftbeam-trash", StringComparison.OrdinalIgnoreCase))
                return true;
            if (seg.EndsWith(".sbpart", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".sbwebpart", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".sbsyncpart", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (patterns.Count == 0)
            return false;

        string fileName = segments[^1];
        foreach (string pat in patterns)
        {
            if (pat.Contains('/'))
            {
                if (WildcardMatch(norm, pat))
                    return true;
            }
            else
            {
                if (WildcardMatch(fileName, pat))
                    return true;
                foreach (string seg in segments)
                {
                    if (WildcardMatch(seg, pat))
                        return true;
                }
            }
        }
        return false;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return input.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static (Dictionary<string, SyncFileRecord> Files, HashSet<string> Dirs) ScanLocalDirectory(
        string localRoot,
        bool includeHidden,
        IReadOnlyList<string> ignorePatterns)
    {
        string fullRoot = Path.GetFullPath(localRoot);
        var files = new Dictionary<string, SyncFileRecord>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(fullRoot))
            return (files, dirs);

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(fullRoot));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = current.EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var item in items)
            {
                try
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((item.Attributes & FileAttributes.System) != 0 && (item.Attributes & FileAttributes.Hidden) != 0) continue;
                    if (!includeHidden && FileShareServer.IsHiddenEntry(item.Name, item.Attributes)) continue;

                    string rel = Path.GetRelativePath(fullRoot, item.FullName).Replace('\\', '/').Trim('/');
                    if (IsIgnored(rel, ignorePatterns)) continue;

                    if (item is DirectoryInfo subDir)
                    {
                        dirs.Add(rel);
                        stack.Push(subDir);
                    }
                    else if (item is FileInfo fi)
                    {
                        files[rel] = new SyncFileRecord(fi.Length, fi.LastWriteTimeUtc);
                    }
                }
                catch
                {
                    // Skip transient locked/inaccessible entries during scan
                }
            }
        }

        return (files, dirs);
    }

    public static (Dictionary<string, (string RemoteFullPath, SyncFileRecord Record)> Files, Dictionary<string, string> Dirs) BuildRemoteMaps(
        IReadOnlyList<RemoteEntry> entries,
        string remoteBaseDir,
        bool includeHidden,
        IReadOnlyList<string> ignorePatterns)
    {
        string remoteBaseNorm = NormalizeRemoteBase(remoteBaseDir);
        var files = new Dictionary<string, (string RemoteFullPath, SyncFileRecord Record)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!includeHidden && entry.IsHidden) continue;

            string rel = MakeRemoteRelative(entry.RelativePath, remoteBaseNorm, entry.Name);
            if (IsIgnored(rel, ignorePatterns)) continue;

            if (entry.IsDirectory)
                dirs[rel] = entry.RelativePath;
            else
                files[rel] = (entry.RelativePath, new SyncFileRecord(entry.Size, entry.LastModifiedUtc));
        }

        return (files, dirs);
    }

    public static AutoSyncPlan ComputePlan(
        string localRoot,
        string remoteBaseDir,
        IReadOnlyDictionary<string, SyncFileRecord> localFiles,
        IReadOnlySet<string> localDirs,
        IReadOnlyDictionary<string, (string RemoteFullPath, SyncFileRecord Record)> remoteFiles,
        IReadOnlyDictionary<string, string> remoteDirs,
        AutoSyncSnapshot baseline,
        AutoSyncPairConfig config)
    {
        string fullLocalRoot = Path.GetFullPath(localRoot);
        string remoteBaseNorm = NormalizeRemoteBase(remoteBaseDir);

        var fileKeys = new HashSet<string>(localFiles.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in remoteFiles.Keys) fileKeys.Add(k);

        var fileActions = new List<AutoSyncAction>();
        var dirCreateActions = new List<AutoSyncAction>();
        var dirDeleteActions = new List<AutoSyncAction>();
        int identicalCount = 0;

        // Track which relative files will exist on local/remote after planned file operations so we never delete non-empty parent dirs
        var survivingLocalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var survivingRemoteFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in fileKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool hasLocal = localFiles.TryGetValue(key, out var localRec);
            bool hasRemote = remoteFiles.TryGetValue(key, out var remotePair);
            bool inBaseline = baseline.Files.TryGetValue(key, out var baseRec);

            string rel = key;
            string localPath = Path.Combine(fullLocalRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            string remotePath = hasRemote ? remotePair.RemoteFullPath : CombineRemote(remoteBaseNorm, rel);

            if (hasLocal && hasRemote)
            {
                if (Matches(localRec!, remotePair.Record))
                {
                    identicalCount++;
                    survivingLocalFiles.Add(rel);
                    survivingRemoteFiles.Add(rel);
                    continue;
                }

                bool localChanged = !inBaseline || !Matches(localRec!, baseRec!);
                bool remoteChanged = !inBaseline || !Matches(remotePair.Record, baseRec!);

                switch (config.Mode)
                {
                    case AutoSyncMode.SendOnly:
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.UploadFile, rel, localPath, remotePath,
                            localRec!.Size, localRec.LastWriteTimeUtc, "Send-Only overwrite"));
                        survivingLocalFiles.Add(rel);
                        survivingRemoteFiles.Add(rel);
                        break;

                    case AutoSyncMode.ReceiveOnly:
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DownloadFile, rel, localPath, remotePath,
                            remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Receive-Only overwrite"));
                        survivingLocalFiles.Add(rel);
                        survivingRemoteFiles.Add(rel);
                        break;

                    case AutoSyncMode.TwoWay:
                        if (inBaseline && localChanged && remoteChanged)
                        {
                            fileActions.Add(new AutoSyncAction(AutoSyncActionKind.ConflictKeepBothDownloadRemote, rel, localPath, remotePath,
                                remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Changed on both computers (keeping conflict copy)"));
                        }
                        else if (localChanged && !remoteChanged)
                        {
                            fileActions.Add(new AutoSyncAction(AutoSyncActionKind.UploadFile, rel, localPath, remotePath,
                                localRec!.Size, localRec.LastWriteTimeUtc, "Updated locally"));
                        }
                        else if (remoteChanged && !localChanged)
                        {
                            fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DownloadFile, rel, localPath, remotePath,
                                remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Updated on remote"));
                        }
                        else
                        {
                            // Initial sync without baseline: newest timestamp wins
                            if (localRec!.LastWriteTimeUtc >= remotePair.Record.LastWriteTimeUtc)
                            {
                                fileActions.Add(new AutoSyncAction(AutoSyncActionKind.UploadFile, rel, localPath, remotePath,
                                    localRec.Size, localRec.LastWriteTimeUtc, "Local file is newer"));
                            }
                            else
                            {
                                fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DownloadFile, rel, localPath, remotePath,
                                    remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Remote file is newer"));
                            }
                        }
                        survivingLocalFiles.Add(rel);
                        survivingRemoteFiles.Add(rel);
                        break;
                }
            }
            else if (hasLocal)
            {
                if (config.Mode == AutoSyncMode.SendOnly)
                {
                    fileActions.Add(new AutoSyncAction(AutoSyncActionKind.UploadFile, rel, localPath, remotePath,
                        localRec!.Size, localRec.LastWriteTimeUtc, "Added locally"));
                    survivingLocalFiles.Add(rel);
                    survivingRemoteFiles.Add(rel);
                }
                else if (config.Mode == AutoSyncMode.ReceiveOnly)
                {
                    if (config.SyncDeletes && inBaseline)
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteLocalFile, rel, localPath, remotePath,
                            localRec!.Size, localRec.LastWriteTimeUtc, "Deleted on remote"));
                    }
                    else
                    {
                        survivingLocalFiles.Add(rel);
                    }
                }
                else // TwoWay
                {
                    if (config.SyncDeletes && inBaseline && Matches(localRec!, baseRec!))
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteLocalFile, rel, localPath, remotePath,
                            localRec!.Size, localRec.LastWriteTimeUtc, "Deleted on remote"));
                    }
                    else
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.UploadFile, rel, localPath, remotePath,
                            localRec!.Size, localRec.LastWriteTimeUtc, inBaseline ? "Edited locally after remote delete" : "Added locally"));
                        survivingLocalFiles.Add(rel);
                        survivingRemoteFiles.Add(rel);
                    }
                }
            }
            else if (hasRemote)
            {
                if (config.Mode == AutoSyncMode.ReceiveOnly)
                {
                    fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DownloadFile, rel, localPath, remotePath,
                        remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Added on remote"));
                    survivingLocalFiles.Add(rel);
                    survivingRemoteFiles.Add(rel);
                }
                else if (config.Mode == AutoSyncMode.SendOnly)
                {
                    if (config.SyncDeletes && inBaseline)
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteRemoteFile, rel, localPath, remotePath,
                            remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Deleted locally"));
                    }
                    else
                    {
                        survivingRemoteFiles.Add(rel);
                    }
                }
                else // TwoWay
                {
                    if (config.SyncDeletes && inBaseline && Matches(remotePair.Record, baseRec!))
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteRemoteFile, rel, localPath, remotePath,
                            remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, "Deleted locally"));
                    }
                    else
                    {
                        fileActions.Add(new AutoSyncAction(AutoSyncActionKind.DownloadFile, rel, localPath, remotePath,
                            remotePair.Record.Size, remotePair.Record.LastWriteTimeUtc, inBaseline ? "Edited on remote after local delete" : "Added on remote"));
                        survivingLocalFiles.Add(rel);
                        survivingRemoteFiles.Add(rel);
                    }
                }
            }
        }

        // Directory creations & deletions (empty folders too!)
        var dirKeys = new HashSet<string>(localDirs, StringComparer.OrdinalIgnoreCase);
        foreach (string k in remoteDirs.Keys) dirKeys.Add(k);

        foreach (string dir in dirKeys.OrderBy(Depth).ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool hasLocalDir = localDirs.Contains(dir);
            bool hasRemoteDir = remoteDirs.TryGetValue(dir, out string? remDirFull);
            bool inBaseDir = baseline.Directories.Contains(dir);

            string localDirFull = Path.Combine(fullLocalRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            string remoteDirFull = hasRemoteDir ? remDirFull! : CombineRemote(remoteBaseNorm, dir);

            if (hasLocalDir && !hasRemoteDir)
            {
                bool anyRemoteFileInside = HasDescendant(survivingRemoteFiles, dir);
                if (config.Mode == AutoSyncMode.SendOnly ||
                    (config.Mode == AutoSyncMode.TwoWay && (!config.SyncDeletes || !inBaseDir)))
                {
                    dirCreateActions.Add(new AutoSyncAction(AutoSyncActionKind.CreateRemoteDir, dir, localDirFull, remoteDirFull, 0, DateTime.MinValue, "Folder created locally"));
                }
                else if (config.SyncDeletes && inBaseDir && !anyRemoteFileInside && !HasDescendant(survivingLocalFiles, dir))
                {
                    dirDeleteActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteLocalDir, dir, localDirFull, remoteDirFull, 0, DateTime.MinValue, "Folder deleted on remote"));
                }
            }
            else if (hasRemoteDir && !hasLocalDir)
            {
                bool anyLocalFileInside = HasDescendant(survivingLocalFiles, dir);
                if (config.Mode == AutoSyncMode.ReceiveOnly ||
                    (config.Mode == AutoSyncMode.TwoWay && (!config.SyncDeletes || !inBaseDir)))
                {
                    dirCreateActions.Add(new AutoSyncAction(AutoSyncActionKind.CreateLocalDir, dir, localDirFull, remoteDirFull, 0, DateTime.MinValue, "Folder created on remote"));
                }
                else if (config.SyncDeletes && inBaseDir && !anyLocalFileInside && !HasDescendant(survivingRemoteFiles, dir))
                {
                    dirDeleteActions.Add(new AutoSyncAction(AutoSyncActionKind.DeleteRemoteDir, dir, localDirFull, remoteDirFull, 0, DateTime.MinValue, "Folder deleted locally"));
                }
            }
        }

        // Order: create dirs (shallow->deep), apply file actions, delete dirs (deep->shallow)
        var actions = new List<AutoSyncAction>(dirCreateActions.Count + fileActions.Count + dirDeleteActions.Count);
        actions.AddRange(dirCreateActions);
        actions.AddRange(fileActions);
        actions.AddRange(dirDeleteActions.OrderByDescending(a => Depth(a.RelativePath)).ThenByDescending(a => a.RelativePath.Length));

        return new AutoSyncPlan(actions, identicalCount);
    }

    public static string BuildConflictCopyPath(string localFullPath)
    {
        string dir = Path.GetDirectoryName(localFullPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(localFullPath);
        string ext = Path.GetExtension(localFullPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return TransferManager.ResolveUniqueLocalPath(Path.Combine(dir, $"{stem}.sync-conflict-{stamp}{ext}"));
    }

    public static bool Matches(SyncFileRecord a, SyncFileRecord b)
    {
        return a.Size == b.Size && Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) <= TimestampToleranceSeconds;
    }

    public static string NormalizeRemoteBase(string remoteBaseDir)
    {
        string norm = (remoteBaseDir ?? string.Empty).Replace('\\', '/');
        if (norm.Length > 1 && !(norm.Length == 3 && norm[1] == ':'))
            norm = norm.TrimEnd('/');
        return norm;
    }

    public static string MakeRemoteRelative(string entryPath, string baseNorm, string fallbackName)
    {
        string rel = (entryPath ?? string.Empty).Replace('\\', '/');
        if (!string.IsNullOrEmpty(baseNorm))
        {
            string prefix = baseNorm.EndsWith('/') ? baseNorm : baseNorm + "/";
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return rel[prefix.Length..].TrimStart('/');
        }
        return string.IsNullOrEmpty(rel) ? fallbackName : rel.TrimStart('/');
    }

    public static string CombineRemote(string baseNorm, string relChild)
    {
        string child = relChild.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(baseNorm)) return child;
        return baseNorm.EndsWith('/') ? baseNorm + child : baseNorm + "/" + child;
    }

    private static int Depth(string relPath)
    {
        int count = 0;
        foreach (char c in relPath)
        {
            if (c == '/') count++;
        }
        return count;
    }

    private static bool HasDescendant(HashSet<string> filePaths, string dirRel)
    {
        string prefix = dirRel.TrimEnd('/') + "/";
        foreach (string f in filePaths)
        {
            if (f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
