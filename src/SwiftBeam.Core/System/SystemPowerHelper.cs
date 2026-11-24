using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwiftBeam.Core.SystemIntegration;

public enum PostTransferAction
{
    DoNothing = 0,
    Disconnect = 1,
    Sleep = 2,
    Shutdown = 3
}

public static class SystemPowerHelper
{
    public static bool ExecutePowerAction(PostTransferAction action)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return action switch
                {
                    PostTransferAction.Sleep => SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false),
                    PostTransferAction.Shutdown => StartHiddenProcess("shutdown", "/s /t 5"),
                    _ => true
                };
            }

            if (OperatingSystem.IsLinux())
            {
                return action switch
                {
                    PostTransferAction.Sleep => StartHiddenProcess("systemctl", "suspend"),
                    PostTransferAction.Shutdown => StartHiddenProcess("systemctl", "poweroff"),
                    _ => true
                };
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartHiddenProcess(string fileName, string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return proc is not null;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
