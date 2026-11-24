using System.Diagnostics;
using System.Reflection;

namespace SwiftBeam.Desktop;

public static class LinuxInstallerHelper
{
    public static (bool Success, string Message) InstallToLinuxDesktop()
    {
        if (!OperatingSystem.IsLinux())
            return (false, "Linux desktop integration is only supported on Linux.");

        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return (false, "Could not locate the running SwiftBeam executable.");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                return (false, "Could not locate the user home directory.");

            var localBinDir = Path.Combine(home, ".local", "bin");
            var appsDir = Path.Combine(home, ".local", "share", "applications");
            var iconDir = Path.Combine(home, ".local", "share", "icons", "hicolor", "256x256", "apps");
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            Directory.CreateDirectory(localBinDir);
            Directory.CreateDirectory(appsDir);
            Directory.CreateDirectory(iconDir);

            var targetExe = Path.Combine(localBinDir, "SwiftBeam");
            var normalizedCurrent = Path.GetFullPath(currentExe);
            var normalizedTarget = Path.GetFullPath(targetExe);

            if (!string.Equals(normalizedCurrent, normalizedTarget, StringComparison.Ordinal))
            {
                var tempExe = targetExe + ".tmp";
                File.Copy(normalizedCurrent, tempExe, overwrite: true);
                SetExecutableMode(tempExe);
                File.Move(tempExe, normalizedTarget, overwrite: true);
            }
            SetExecutableMode(normalizedTarget);

            var iconPath = Path.Combine(iconDir, "swiftbeam.png");
            ExtractEmbeddedIcon(iconPath);

            var desktopEntry = string.Join('\n',
                "[Desktop Entry]",
                "Version=1.0",
                "Type=Application",
                "Name=SwiftBeam",
                "Comment=Fast cross-platform parallel file transfer over Wi-Fi, LAN & direct",
                $"Exec=\"{normalizedTarget}\"",
                "Icon=swiftbeam",
                "Terminal=false",
                "Categories=Network;FileTransfer;Utility;",
                "StartupWMClass=SwiftBeam",
                "StartupNotify=true",
                string.Empty);

            var appDesktopPath = Path.Combine(appsDir, "swiftbeam.desktop");
            File.WriteAllText(appDesktopPath, desktopEntry);
            SetExecutableMode(appDesktopPath);

            if (!string.IsNullOrWhiteSpace(desktopDir) && Directory.Exists(desktopDir))
            {
                var userDesktopPath = Path.Combine(desktopDir, "SwiftBeam.desktop");
                File.WriteAllText(userDesktopPath, desktopEntry);
                SetExecutableMode(userDesktopPath);
                TryRun("gio", $"set \"{userDesktopPath}\" metadata::trusted true");
            }

            TryRun("update-desktop-database", $"\"{appsDir}\"");
            TryRun("gtk-update-icon-cache", $"-f -t \"{Path.Combine(home, ".local", "share", "icons", "hicolor")}\"");

            return (true, $"Installed to {normalizedTarget}, App Menu, and Desktop.");
        }
        catch (Exception ex)
        {
            return (false, $"Install failed: {ex.Message}");
        }
    }

    private static void ExtractEmbeddedIcon(string iconPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("SwiftBeam.png");
        if (stream is null)
            return;

        using var fs = File.Create(iconPath);
        stream.CopyTo(fs);
    }

    private static void SetExecutableMode(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            TryRun("chmod", $"+x \"{path}\"");
        }
    }

    private static void TryRun(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            proc?.WaitForExit(3000);
        }
        catch
        {
            // Optional desktop helper not available on every distro.
        }
    }
}
