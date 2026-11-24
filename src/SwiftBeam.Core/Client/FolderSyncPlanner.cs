using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Server;

namespace SwiftBeam.Core.Client;

public enum SyncDirection
{
    LocalToRemote = 0,
    RemoteToLocal = 1,
    TwoWayNewest = 2
}

public enum SyncActionKind
{
    CopyToRemote,
    CopyToLocal,
    SkipIdentical
}

public sealed record SyncDiffItem(
    string RelativePath,
    SyncActionKind Action,
    long SizeBytes,
    string Reason,
    string LocalFullPath,
    string RemoteFullPath,
    RemoteEntry? RemoteEntry);

public sealed record SyncPlanResult(List<SyncDiffItem> Items, int IdenticalCount)
{
    public int ToSendCount => Items.Count(x => x.Action == SyncActionKind.CopyToRemote);
    public int ToPullCount => Items.Count(x => x.Action == SyncActionKind.CopyToLocal);
    public long TotalBytes => Items.Where(x => x.Action != SyncActionKind.SkipIdentical).Sum(x => x.SizeBytes);
}

public static class FolderSyncPlanner
{
    private const double TimestampToleranceSeconds = 2.0;

    public static SyncPlanResult PlanSync(
        string localDir,
        IReadOnlyList<RemoteEntry> remoteEntriesRecursive,
        string remoteBaseDir,
        SyncDirection direction,
        bool includeHidden = false)
    {
        if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir))
            throw new DirectoryNotFoundException($"Local folder does not exist: {localDir}");

        string localRoot = Path.GetFullPath(localDir);
        string remoteBaseNorm = NormalizeRemoteBase(remoteBaseDir);

        var localFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(localRoot));
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

                    if (item is DirectoryInfo subDir)
                        stack.Push(subDir);
                    else if (item is FileInfo fi)
                    {
                        string rel = Path.GetRelativePath(localRoot, fi.FullName).Replace('\\', '/');
                        localFiles[rel] = fi;
                    }
                }
                catch { }
            }
        }

        var remoteFiles = new Dictionary<string, (string RelKey, RemoteEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in remoteEntriesRecursive)
        {
            if (entry.IsDirectory) continue;
            if (!includeHidden && entry.IsHidden) continue;

            string rel = MakeRemoteRelative(entry.RelativePath, remoteBaseNorm, entry.Name);
            remoteFiles[rel] = (rel, entry);
        }

        var keys = new HashSet<string>(localFiles.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in remoteFiles.Keys)
            keys.Add(k);

        var diffItems = new List<SyncDiffItem>();
        int identicalCount = 0;

        foreach (string key in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool hasLocal = localFiles.TryGetValue(key, out var localFi);
            bool hasRemote = remoteFiles.TryGetValue(key, out var remotePair);

            string relDisplay = hasLocal
                ? Path.GetRelativePath(localRoot, localFi!.FullName).Replace('\\', '/')
                : remotePair.RelKey;

            string targetLocal = Path.Combine(localRoot, relDisplay.Replace('/', Path.DirectorySeparatorChar));
            string targetRemote = CombineRemote(remoteBaseNorm, relDisplay);

            if (hasLocal && hasRemote)
            {
                var remoteEntry = remotePair.Entry;
                bool sameSize = localFi!.Length == remoteEntry.Size;
                double deltaSec = (localFi.LastWriteTimeUtc - remoteEntry.LastModifiedUtc).TotalSeconds;
                bool sameTime = Math.Abs(deltaSec) <= TimestampToleranceSeconds;

                if (sameSize && sameTime)
                {
                    identicalCount++;
                    continue;
                }

                switch (direction)
                {
                    case SyncDirection.LocalToRemote:
                        diffItems.Add(new SyncDiffItem(
                            relDisplay,
                            SyncActionKind.CopyToRemote,
                            localFi.Length,
                            deltaSec > TimestampToleranceSeconds ? "Local is newer" : "Size/timestamp differs",
                            localFi.FullName,
                            targetRemote,
                            remoteEntry));
                        break;

                    case SyncDirection.RemoteToLocal:
                        diffItems.Add(new SyncDiffItem(
                            relDisplay,
                            SyncActionKind.CopyToLocal,
                            remoteEntry.Size,
                            deltaSec < -TimestampToleranceSeconds ? "Remote is newer" : "Size/timestamp differs",
                            targetLocal,
                            remoteEntry.RelativePath,
                            remoteEntry));
                        break;

                    case SyncDirection.TwoWayNewest:
                        if (deltaSec > TimestampToleranceSeconds || (Math.Abs(deltaSec) <= TimestampToleranceSeconds && localFi.Length >= remoteEntry.Size))
                        {
                            diffItems.Add(new SyncDiffItem(
                                relDisplay,
                                SyncActionKind.CopyToRemote,
                                localFi.Length,
                                deltaSec > TimestampToleranceSeconds ? "Local is newer" : "Local size differs",
                                localFi.FullName,
                                targetRemote,
                                remoteEntry));
                        }
                        else
                        {
                            diffItems.Add(new SyncDiffItem(
                                relDisplay,
                                SyncActionKind.CopyToLocal,
                                remoteEntry.Size,
                                deltaSec < -TimestampToleranceSeconds ? "Remote is newer" : "Remote size differs",
                                targetLocal,
                                remoteEntry.RelativePath,
                                remoteEntry));
                        }
                        break;
                }
            }
            else if (hasLocal)
            {
                if (direction is SyncDirection.LocalToRemote or SyncDirection.TwoWayNewest)
                {
                    diffItems.Add(new SyncDiffItem(
                        relDisplay,
                        SyncActionKind.CopyToRemote,
                        localFi!.Length,
                        "Only on Local",
                        localFi.FullName,
                        targetRemote,
                        null));
                }
            }
            else if (hasRemote)
            {
                if (direction is SyncDirection.RemoteToLocal or SyncDirection.TwoWayNewest)
                {
                    diffItems.Add(new SyncDiffItem(
                        relDisplay,
                        SyncActionKind.CopyToLocal,
                        remotePair.Entry.Size,
                        "Only on Remote",
                        targetLocal,
                        remotePair.Entry.RelativePath,
                        remotePair.Entry));
                }
            }
        }

        return new SyncPlanResult(diffItems, identicalCount);
    }

    private static string NormalizeRemoteBase(string remoteBaseDir)
    {
        string norm = (remoteBaseDir ?? string.Empty).Replace('\\', '/');
        if (norm.Length > 1 && !(norm.Length == 3 && norm[1] == ':'))
            norm = norm.TrimEnd('/');
        return norm;
    }

    private static string MakeRemoteRelative(string entryPath, string baseNorm, string fallbackName)
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

    private static string CombineRemote(string baseNorm, string relChild)
    {
        string child = relChild.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(baseNorm)) return child;
        return baseNorm.EndsWith('/') ? baseNorm + child : baseNorm + "/" + child;
    }
}
