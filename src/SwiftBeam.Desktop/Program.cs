using System.Runtime.InteropServices;
using Avalonia;

namespace SwiftBeam.Desktop;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash(ex);
        };

        try
        {
            if (OperatingSystem.IsLinux() && args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
            {
                var (ok, message) = LinuxInstallerHelper.InstallToLinuxDesktop();
                Console.WriteLine(message);
                Environment.ExitCode = ok ? 0 : 1;
                return;
            }

            using var instanceGuard = SingleInstanceGuard.TryAcquire();
            if (!instanceGuard.IsPrimaryInstance)
            {
                instanceGuard.TrySignalExistingInstance();
                return;
            }

            App.InstanceGuard = instanceGuard;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            string logPath = LogCrash(ex);
            ShowFatalStartupError(ex, logPath);
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string LogCrash(Exception ex)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "SwiftBeam-crash.log");
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { }
        return logPath;
    }

    private static void ShowFatalStartupError(Exception ex, string logPath)
    {
        string message = $"SwiftBeam could not start:\n\n{ex.GetType().Name}: {ex.Message}\n\nFull details saved to:\n{logPath}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { MessageBoxW(IntPtr.Zero, message, "SwiftBeam Startup Error", 0x00000010); } catch { }
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

