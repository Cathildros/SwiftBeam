using System.Text.Json;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;

namespace SwiftBeam.Core.Settings;

public enum FileConflictPolicy
{
    SkipIdentical = 0,
    Overwrite = 1,
    RenameBoth = 2
}

public sealed class SavedPeerEntry
{
    public string DeviceName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = ProtocolConstants.DefaultTransferPort;
    public bool UseTls { get; set; } = true;
    public string SavedPin { get; set; } = string.Empty;
    public DateTime LastConnectedUtc { get; set; } = DateTime.UtcNow;

    public string DisplayLabel => string.IsNullOrWhiteSpace(DeviceName)
        ? $"★ {Host}:{Port}"
        : $"★ {DeviceName} ({Host})";
}

public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = ProtocolConstants.DefaultTransferPort;
    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
    public string SharedDirectory { get; set; } = GetDefaultShareDir();
    public string DownloadDirectory { get; set; } = GetDefaultDownloadDir();
    public bool AllowFullFilesystemAccess { get; set; } = true;
    public bool RequirePin { get; set; } = true;
    public string Pin { get; set; } = PinAuth.GeneratePin();
    public bool AllowUnencryptedConnections { get; set; } = true;
    public bool PreferTls { get; set; } = true;
    public int ParallelWorkers { get; set; } = 6;
    public int ChunkSizeMb { get; set; } = 16;
    public bool VerifySha256OnComplete { get; set; } = true;
    public bool AutoStartServer { get; set; } = true;
    public FileConflictPolicy ConflictPolicy { get; set; } = FileConflictPolicy.SkipIdentical;
    public int MaxSpeedMBps { get; set; } = 0; // 0 = Unlimited
    public bool MinimizeToTrayOnClose { get; set; } = false;
    public bool NotifyOnTransferComplete { get; set; } = true;
    public string UpdateRepo { get; set; } = "Cathildros/SwiftBeam";
    public bool AutoCheckUpdates { get; set; } = true;
    public List<SavedPeerEntry> SavedPeers { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetSettingsFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "SwiftBeam", "settings.json");
    }

    public static string GetDefaultShareDir()
    {
        // Avoid MyDocuments directly so Windows Defender Controlled Folder Access / OneDrive locks never block startup
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, "SwiftBeamShare");
        return Path.Combine(Path.GetTempPath(), "SwiftBeamShare");
    }

    public static string GetDefaultDownloadDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            string downloads = Path.Combine(home, "Downloads");
            return Directory.Exists(downloads)
                ? Path.Combine(downloads, "SwiftBeam")
                : Path.Combine(home, "SwiftBeamDownloads");
        }
        return Path.Combine(Path.GetTempPath(), "SwiftBeamDownloads");
    }

    public static string EnsureWritableDirectory(string preferredPath, string fallbackFolderName)
    {
        try
        {
            Directory.CreateDirectory(preferredPath);
            return preferredPath;
        }
        catch
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                try
                {
                    string fallback = Path.Combine(localAppData, "SwiftBeam", fallbackFolderName);
                    Directory.CreateDirectory(fallback);
                    return fallback;
                }
                catch { }
            }

            string tempFallback = Path.Combine(Path.GetTempPath(), "SwiftBeam", fallbackFolderName);
            Directory.CreateDirectory(tempFallback);
            return tempFallback;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupt settings file
        }

        var defaults = new AppSettings();
        defaults.Sanitize();
        defaults.Save();
        return defaults;
    }

    public void Sanitize()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
            DeviceName = Environment.MachineName;
        if (ListenPort is < 1024 or > 65535)
            ListenPort = ProtocolConstants.DefaultTransferPort;
        if (DiscoveryPort is < 1024 or > 65535)
            DiscoveryPort = ProtocolConstants.DefaultDiscoveryPort;
        if (string.IsNullOrWhiteSpace(SharedDirectory))
            SharedDirectory = GetDefaultShareDir();
        if (string.IsNullOrWhiteSpace(DownloadDirectory))
            DownloadDirectory = GetDefaultDownloadDir();
        if (string.IsNullOrWhiteSpace(Pin))
            Pin = PinAuth.GeneratePin();
        ParallelWorkers = Math.Clamp(ParallelWorkers, 1, 16);
        ChunkSizeMb = Math.Clamp(ChunkSizeMb, 1, 64);
        MaxSpeedMBps = Math.Clamp(MaxSpeedMBps, 0, 10_000);
        if (string.IsNullOrWhiteSpace(UpdateRepo))
            UpdateRepo = "Cathildros/SwiftBeam";
        SavedPeers ??= [];
    }

    public void UpsertSavedPeer(string deviceName, string host, int port, bool useTls, string? pinToRemember)
    {
        SavedPeers ??= [];
        var existing = SavedPeers.FirstOrDefault(p =>
            p.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && p.Port == port);

        if (existing is null)
        {
            existing = new SavedPeerEntry
            {
                DeviceName = deviceName,
                Host = host,
                Port = port,
                UseTls = useTls,
                SavedPin = pinToRemember ?? string.Empty,
                LastConnectedUtc = DateTime.UtcNow
            };
            SavedPeers.Insert(0, existing);
        }
        else
        {
            existing.DeviceName = string.IsNullOrWhiteSpace(deviceName) ? existing.DeviceName : deviceName;
            existing.UseTls = useTls;
            if (pinToRemember is not null)
                existing.SavedPin = pinToRemember;
            existing.LastConnectedUtc = DateTime.UtcNow;
            SavedPeers.Remove(existing);
            SavedPeers.Insert(0, existing);
        }

        while (SavedPeers.Count > 15)
            SavedPeers.RemoveAt(SavedPeers.Count - 1);

        Save();
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsFilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Non-fatal if settings path is read-only
        }
    }
}
