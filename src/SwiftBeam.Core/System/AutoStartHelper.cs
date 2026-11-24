using System.Runtime.InteropServices;
using System.Text;

namespace SwiftBeam.Core.SystemIntegration;

/// <summary>
/// Configures launching SwiftBeam minimized to tray at user sign-in on Windows (HKCU Run key via
/// advapi32 P/Invoke) and Linux (~/.config/autostart/swiftbeam.desktop).
/// </summary>
public static class AutoStartHelper
{
    private const string WindowsRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppEntryName = "SwiftBeam";

    private static readonly UIntPtr HKEY_CURRENT_USER = (UIntPtr)0x80000001u;
    private const int KEY_SET_VALUE = 0x0002;
    private const uint REG_SZ = 1;

    public static bool TrySetAutoStart(bool enabled, string? executablePath = null)
    {
        string exe = executablePath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
            return SetWindowsAutoStart(enabled, exe);

        if (OperatingSystem.IsLinux())
            return SetLinuxAutoStart(enabled, exe);

        return false;
    }

    private static bool SetWindowsAutoStart(bool enabled, string exePath)
    {
        UIntPtr hKey = UIntPtr.Zero;
        try
        {
            int rc = RegCreateKeyExW(
                HKEY_CURRENT_USER,
                WindowsRunSubKey,
                0,
                null,
                0,
                KEY_SET_VALUE,
                IntPtr.Zero,
                out hKey,
                out _);

            if (rc != 0 || hKey == UIntPtr.Zero)
                return false;

            if (enabled)
            {
                string command = $"\"{exePath}\" --minimized";
                byte[] bytes = Encoding.Unicode.GetBytes(command + '\0');
                return RegSetValueExW(hKey, AppEntryName, 0, REG_SZ, bytes, (uint)bytes.Length) == 0;
            }
            else
            {
                int delRc = RegDeleteValueW(hKey, AppEntryName);
                // 2 = ERROR_FILE_NOT_FOUND (already absent)
                return delRc == 0 || delRc == 2;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hKey != UIntPtr.Zero)
                RegCloseKey(hKey);
        }
    }

    private static bool SetLinuxAutoStart(bool enabled, string exePath)
    {
        try
        {
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            string autostartDir = Path.Combine(configHome, "autostart");
            string desktopPath = Path.Combine(autostartDir, "swiftbeam.desktop");

            if (!enabled)
            {
                if (File.Exists(desktopPath)) File.Delete(desktopPath);
                return true;
            }

            Directory.CreateDirectory(autostartDir);
            string execQuoted = exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
            string content =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=SwiftBeam\n" +
                "Comment=Fast cross-platform parallel file transfer\n" +
                $"Exec={execQuoted} --minimized\n" +
                "Icon=swiftbeam\n" +
                "Terminal=false\n" +
                "X-GNOME-Autostart-enabled=true\n";

            File.WriteAllText(desktopPath, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(
        UIntPtr hKey,
        string lpSubKey,
        uint Reserved,
        string? lpClass,
        uint dwOptions,
        int samDesired,
        IntPtr lpSecurityAttributes,
        out UIntPtr phkResult,
        out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(
        UIntPtr hKey,
        string lpValueName,
        uint Reserved,
        uint dwType,
        byte[] lpData,
        uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValueW(
        UIntPtr hKey,
        string lpValueName);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(UIntPtr hKey);
}
