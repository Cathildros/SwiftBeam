using System.Text.Json;
using SwiftBeam.Core.Client;

namespace SwiftBeam.Core.Settings;

public sealed record TransferHistoryEntry(
    string Id,
    DateTime CompletedUtc,
    TransferDirection Direction,
    string FileName,
    string LocalPath,
    string RemotePath,
    string PeerName,
    long TotalBytes,
    double AverageBytesPerSec,
    TransferStatus FinalStatus);

public static class TransferHistoryStore
{
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static List<TransferHistoryEntry> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return [];

            var entries = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(File.ReadAllText(filePath), JsonOptions);
            return entries?
                .OrderByDescending(e => e.CompletedUtc)
                .Take(MaxEntries)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(string filePath, IReadOnlyList<TransferHistoryEntry> entries)
    {
        try
        {
            var trimmed = entries
                .OrderByDescending(e => e.CompletedUtc)
                .Take(MaxEntries)
                .ToList();

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(trimmed, JsonOptions));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch
        {
            // History write failure should not fail transfers
        }
    }

    public static List<TransferHistoryEntry> Append(string filePath, TransferHistoryEntry entry)
    {
        var list = Load(filePath);
        list.RemoveAll(e => e.Id == entry.Id);
        list.Insert(0, entry);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        Save(filePath, list);
        return list;
    }
}
