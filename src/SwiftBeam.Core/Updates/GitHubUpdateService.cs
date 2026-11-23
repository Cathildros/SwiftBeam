using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwiftBeam.Core.Updates;

public sealed record UpdateReleaseInfo(
    Version Version,
    string TagName,
    string ReleaseNotes,
    string ReleaseUrl,
    string AssetName,
    string DownloadUrl,
    long AssetSizeBytes,
    string? ExpectedSha256);

/// <summary>
/// Cross-platform GitHub Releases self-updater for SwiftBeam (Windows Setup/EXE &amp; Linux binary).
/// Fetches the latest non-draft release from api.github.com, optionally verifies SHA-256 against
/// sha256sums.txt, and performs a seamless background swap + relaunch.
/// </summary>
public sealed class GitHubUpdateService
{
    public const string DefaultRepo = "Cathildros/SwiftBeam";

    private static HttpClient CreateClient(Version currentVersion)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SwiftBeam/{currentVersion}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdateService).Assembly;
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var parsedInfo = ParseVersion(infoAttr?.InformationalVersion);
        if (parsedInfo is not null)
            return parsedInfo;

        var asmVer = asm.GetName().Version;
        if (asmVer is not null)
            return new Version(Math.Max(0, asmVer.Major), Math.Max(0, asmVer.Minor), Math.Max(0, asmVer.Build));

        return new Version(1, 0, 0);
    }

    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0) s = s[..plusIdx];

        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0) s = s[..dashIdx];

        string[] parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
            return null;

        if (!int.TryParse(parts[0], out int major) || major < 0) return null;
        if (!int.TryParse(parts[1], out int minor) || minor < 0) return null;

        int build = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out build) || build < 0))
            return null;

        return new Version(major, minor, build);
    }

    public static string NormalizeRepoSlug(string? repo)
    {
        string r = (repo ?? string.Empty).Trim();
        if (r.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            r = r["https://github.com/".Length..];
        else if (r.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            r = r["http://github.com/".Length..];

        r = r.Trim('/');
        if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            r = r[..^4];

        return r;
    }

    public static UpdateReleaseInfo? ParseReleaseJson(
        string json,
        Version currentVersion,
        bool isWindows,
        bool isLinux,
        out string? checksumsAssetUrl)
    {
        checksumsAssetUrl = null;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            return null;
        if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
            return null;

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? string.Empty) : string.Empty;
        var latestVersion = ParseVersion(tagName);
        if (latestVersion is null || latestVersion <= currentVersion)
            return null;

        string releaseUrl = root.TryGetProperty("html_url", out var htmlProp) ? (htmlProp.GetString() ?? string.Empty) : string.Empty;
        string releaseNotes = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? string.Empty) : string.Empty;

        if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
            return null;

        string? preferredName = null;
        string? preferredUrl = null;
        long preferredSize = 0;

        string? fallbackName = null;
        string? fallbackUrl = null;
        long fallbackSize = 0;

        foreach (var asset in assetsProp.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
            string url = asset.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
            long size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long sVal) ? sVal : 0;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                continue;

            if (name.Equals("sha256sums.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksumsAssetUrl = url;
                continue;
            }

            if (isWindows)
            {
                if (name.Equals("SwiftBeam-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    preferredName = name;
                    preferredUrl = url;
                    preferredSize = size;
                }
                else if (name.Equals("SwiftBeam.exe", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackName = name;
                    fallbackUrl = url;
                    fallbackSize = size;
                }
            }
            else if (isLinux)
            {
                if (name.Equals("SwiftBeam", StringComparison.OrdinalIgnoreCase))
                {
                    preferredName = name;
                    preferredUrl = url;
                    preferredSize = size;
                }
            }
        }

        string? chosenName = preferredName ?? fallbackName;
        string? chosenUrl = preferredUrl ?? fallbackUrl;
        long chosenSize = preferredName is not null ? preferredSize : fallbackSize;

        if (string.IsNullOrEmpty(chosenName) || string.IsNullOrEmpty(chosenUrl))
            return null;

        return new UpdateReleaseInfo(
            latestVersion,
            tagName,
            releaseNotes,
            releaseUrl,
            chosenName,
            chosenUrl,
            chosenSize,
            null);
    }

    public static string? ExtractSha256ForAsset(string? checksumsText, string assetName)
    {
        if (string.IsNullOrWhiteSpace(checksumsText) || string.IsNullOrWhiteSpace(assetName))
            return null;

        string[] lines = checksumsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length < 66 || line.StartsWith('#'))
                continue;

            string candidateHash = line[..64];
            if (!IsHex64(candidateHash))
                continue;

            if (!char.IsWhiteSpace(line[64]))
                continue;

            string filePart = line[65..].TrimStart();
            if (filePart.StartsWith('*'))
                filePart = filePart[1..];

            if (filePart.Equals(assetName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(filePart).Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return candidateHash.ToLowerInvariant();
            }
        }

        return null;
    }

    private static bool IsHex64(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    public async Task<UpdateReleaseInfo?> CheckForUpdateAsync(string repo, Version currentVersion, CancellationToken ct = default)
    {
        string slug = NormalizeRepoSlug(repo);
        string[] segs = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length != 2)
            throw new ArgumentException("Repository must be in 'owner/repo' format (e.g. Cathildros/SwiftBeam).");

        using var http = CreateClient(currentVersion);
        string apiUrl = $"https://api.github.com/repos/{slug}/releases/latest";

        using var resp = await http.GetAsync(apiUrl, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var info = ParseReleaseJson(
            json,
            currentVersion,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            out string? checksumsUrl);

        if (info is null)
            return null;

        if (!string.IsNullOrEmpty(checksumsUrl))
        {
            try
            {
                string checksums = await http.GetStringAsync(checksumsUrl, ct).ConfigureAwait(false);
                string? expected = ExtractSha256ForAsset(checksums, info.AssetName);
                if (!string.IsNullOrEmpty(expected))
                    info = info with { ExpectedSha256 = expected };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Non-fatal if checksums file cannot be fetched separately
            }
        }

        return info;
    }

    public async Task<string> DownloadUpdateAsync(UpdateReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string safeTag = string.Concat(release.TagName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safeTag))
            safeTag = release.Version.ToString();

        string stageDir = Path.Combine(Path.GetTempPath(), $"SwiftBeam-Update-{safeTag}");
        Directory.CreateDirectory(stageDir);

        string destPath = Path.Combine(stageDir, release.AssetName);
        if (File.Exists(destPath))
            File.Delete(destPath);

        using var http = CreateClient(GetCurrentVersion());
        http.Timeout = TimeSpan.FromMinutes(10);

        using var resp = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? release.AssetSizeBytes;
        long downloaded = 0;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];

        await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                downloaded += read;

                if (totalBytes > 0)
                    progress?.Report(Math.Clamp((double)downloaded / totalBytes * 100.0, 0.0, 100.0));
            }
        }

        progress?.Report(100.0);

        if (!string.IsNullOrWhiteSpace(release.ExpectedSha256))
        {
            string actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actualSha.Equals(release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(destPath); } catch { }
                throw new InvalidDataException(
                    $"Update verification failed: SHA-256 mismatch for {release.AssetName} (expected {release.ExpectedSha256[..12]}..., got {actualSha[..12]}...).");
            }
        }

        return destPath;
    }

    public static void LaunchUpdateAndExit(string downloadedFilePath, Action requestAppExit)
    {
        if (OperatingSystem.IsWindows())
        {
            LaunchWindowsUpdate(downloadedFilePath);
            requestAppExit();
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LaunchLinuxUpdate(downloadedFilePath);
            requestAppExit();
            return;
        }

        throw new PlatformNotSupportedException("Self-update is supported on Windows and Linux.");
    }

    private static void LaunchWindowsUpdate(string downloadedFilePath)
    {
        string fileName = Path.GetFileName(downloadedFilePath);
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fileName.Equals("SwiftBeam-Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            bool isInstalledCopy = File.Exists(Path.Combine(appDir, "Uninstall.exe"));
            string args = isInstalledCopy
                ? $"/S /LAUNCH \"/DIR={appDir}\""
                : "/S /LAUNCH";

            Process.Start(new ProcessStartInfo
            {
                FileName = downloadedFilePath,
                Arguments = args,
                UseShellExecute = true
            });
            return;
        }

        // Portable SwiftBeam.exe swap helper
        string currentExe = Environment.ProcessPath ?? Path.Combine(appDir, "SwiftBeam.exe");
        string cmdArgs = $"/c ping 127.0.0.1 -n 3 > nul & move /y \"{downloadedFilePath}\" \"{currentExe}\" & start \"\" \"{currentExe}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    [SupportedOSPlatform("linux")]
    private static void LaunchLinuxUpdate(string downloadedFilePath)
    {
        try
        {
            File.SetUnixFileMode(
                downloadedFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }

        string targetPath = ResolveLinuxUpdateTargetPath();
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        string dlEsc = EscapeSingleQuotes(downloadedFilePath);
        string tgtEsc = EscapeSingleQuotes(targetPath);
        string script = $"sleep 1; mv -f '{dlEsc}' '{tgtEsc}' && chmod 755 '{tgtEsc}' && nohup '{tgtEsc}' >/dev/null 2>&1 &";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        Process.Start(psi);
    }

    private static string ResolveLinuxUpdateTargetPath()
    {
        string? proc = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(proc) && IsDirectoryWritable(Path.GetDirectoryName(proc)))
            return proc;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "bin", "SwiftBeam");
    }

    private static bool IsDirectoryWritable(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        try
        {
            string probe = Path.Combine(dir, $".sbwrite-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeSingleQuotes(string s) => s.Replace("'", "'\\''");
}
