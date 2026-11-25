using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwiftBeam.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayIcon : IDisposable
{
    private const int WmUser = 0x0400;
    private const int WmTrayCallback = WmUser + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly nint _hInstance;
    private nint _hwnd;
    private nint _hIcon;
    private bool _iconAdded;
    private bool _disposed;

    public event Action? LeftClicked;
    public event Action<int, int>? RightClicked;

    public WindowsTrayIcon(string tooltip)
    {
        _wndProc = WindowProc;
        _hInstance = GetModuleHandleW(null);
        _className = "SwiftBeamTrayMsg_" + Guid.NewGuid().ToString("N")[..10];

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className
        };

        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Hidden unshown top-level window receives Shell_NotifyIconW callbacks cleanly on Avalonia's STA dispatcher thread
        _hwnd = CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0, nint.Zero, nint.Zero, _hInstance, nint.Zero);
        if (_hwnd == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _hIcon = LoadSwiftBeamIconHandle();

        var nid = CreateNotifyData(tooltip);
        nid.uFlags = NifMessage | NifIcon | NifTip;
        _iconAdded = Shell_NotifyIconW(NimAdd, ref nid);
    }

    public void SetTooltip(string tooltip)
    {
        if (_disposed || !_iconAdded || _hwnd == nint.Zero)
            return;

        var nid = CreateNotifyData(tooltip);
        nid.uFlags = NifTip;
        Shell_NotifyIconW(NimModify, ref nid);
    }

    private NOTIFYICONDATAW CreateNotifyData(string tooltip)
    {
        string clipped = string.IsNullOrWhiteSpace(tooltip) ? "SwiftBeam" : tooltip;
        if (clipped.Length > 120) clipped = clipped[..120];

        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = clipped,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private nint WindowProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmTrayCallback)
        {
            int mouseMsg = unchecked((int)(lParam.ToInt64() & 0xFFFF));
            if (mouseMsg is WmLButtonUp or WmLButtonDblClk)
            {
                LeftClicked?.Invoke();
            }
            else if (mouseMsg is WmRButtonUp or WmContextMenu)
            {
                GetCursorPos(out var pt);
                RightClicked?.Invoke(pt.X, pt.Y);
            }
            return nint.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static nint LoadSwiftBeamIconHandle()
    {
        string? tempIco = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwiftBeam.ico");
            if (stream is not null)
            {
                tempIco = Path.Combine(Path.GetTempPath(), $"sb_tray_{Environment.ProcessId}.ico");
                using (var fs = File.Create(tempIco))
                    stream.CopyTo(fs);

                nint hIcon = LoadImageW(nint.Zero, tempIco, ImageIcon, 32, 32, LrLoadFromFile | LrDefaultSize);
                if (hIcon != nint.Zero)
                    return hIcon;
            }
        }
        catch { }
        finally
        {
            if (tempIco is not null)
            {
                try { File.Delete(tempIco); } catch { }
            }
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_iconAdded && _hwnd != nint.Zero)
        {
            var nid = CreateNotifyData(string.Empty);
            Shell_NotifyIconW(NimDelete, ref nid);
            _iconAdded = false;
        }
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
        if (_hIcon != nint.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }
        UnregisterClassW(_className, _hInstance);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
