using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwiftBeam.Core.Client;

internal static class SparseFileHelper
{
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>
    /// Marks a new partial file as sparse on Windows NTFS so SetLength does not synchronously zero-fill
    /// multi-gigabyte allocations before parallel chunk writes begin. Safely no-ops on Linux or FAT32/exFAT.
    /// </summary>
    public static void TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed)
            return;

        try
        {
            DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            // Non-fatal on filesystems that do not support sparse files (FAT32/exFAT)
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
