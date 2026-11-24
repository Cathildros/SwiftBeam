using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwiftBeam.Core.SystemIntegration;

/// <summary>
/// Cross-platform system sleep prevention while active transfers are running.
/// Windows uses SetThreadExecutionState; Linux spawns systemd-inhibit when available.
/// </summary>
public sealed class SleepInhibitor : IDisposable
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private bool _active;
    private Process? _linuxInhibitProcess;

    public bool IsActive => _active;

    public void SetActive(bool preventSleep)
    {
        if (_active == preventSleep)
            return;

        _active = preventSleep;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetThreadExecutionState(preventSleep ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
            }
            catch { }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (preventSleep)
                StartLinuxInhibit();
            else
                StopLinuxInhibit();
        }
    }

    private void StartLinuxInhibit()
    {
        StopLinuxInhibit();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemd-inhibit",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--what=idle:sleep");
            psi.ArgumentList.Add("--who=SwiftBeam");
            psi.ArgumentList.Add("--why=File transfer in progress");
            psi.ArgumentList.Add("--mode=block");
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add("infinity");

            _linuxInhibitProcess = Process.Start(psi);
        }
        catch
        {
            _linuxInhibitProcess = null;
        }
    }

    private void StopLinuxInhibit()
    {
        if (_linuxInhibitProcess is null) return;
        try
        {
            if (!_linuxInhibitProcess.HasExited)
                _linuxInhibitProcess.Kill(entireProcessTree: true);
        }
        catch { }
        try { _linuxInhibitProcess.Dispose(); } catch { }
        _linuxInhibitProcess = null;
    }

    public void Dispose()
    {
        SetActive(false);
        StopLinuxInhibit();
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
