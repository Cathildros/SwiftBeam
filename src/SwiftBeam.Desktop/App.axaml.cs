using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public partial class App : Application
{
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    private TrayIcon? _trayIcon;
    private WindowsTrayIcon? _windowsTrayIcon;
    private bool _isExplicitExit;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm };

            WindowIcon? appIcon = null;
            try
            {
                using var iconStream = AssetLoader.Open(new Uri("avares://SwiftBeam/Assets/SwiftBeam.png"));
                appIcon = new WindowIcon(iconStream);
            }
            catch
            {
                try { appIcon = InputDialog.CreateAppIcon(); } catch { }
            }
            if (appIcon is not null)
                win.Icon = appIcon;

            var startMinimized = desktop.Args?.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) == true;

            // Intercept window close when MinimizeToTrayOnClose is enabled
            win.Closing += (_, e) =>
            {
                if (!_isExplicitExit && vm.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    win.Hide();
                }
                else if (!_isExplicitExit && !vm.MinimizeToTrayOnClose)
                {
                    _isExplicitExit = true;
                    desktop.Shutdown();
                }
            };

            // Cross-platform System Tray icon (Windows pill popup + Linux native tray)
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _windowsTrayIcon = new WindowsTrayIcon(vm.TrayTooltipText);
                    _windowsTrayIcon.LeftClicked += () =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMainWindow(win));
                    _windowsTrayIcon.RightClicked += (x, y) =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            TrayMenuWindow.ShowAt(
                                x,
                                y,
                                vm,
                                appIcon,
                                () => ShowMainWindow(win),
                                async () =>
                                {
                                    if (win.Clipboard is not null)
                                        await win.Clipboard.SetTextAsync(vm.HostPin);
                                    vm.ShowToast("Pairing PIN copied to clipboard");
                                },
                                () =>
                                {
                                    _isExplicitExit = true;
                                    desktop.Shutdown();
                                }));

                    vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(MainWindowViewModel.TrayTooltipText)
                            && OperatingSystem.IsWindows()
                            && _windowsTrayIcon is not null)
                        {
                            _windowsTrayIcon.SetTooltip(vm.TrayTooltipText);
                        }
                    };
                }
                catch
                {
                    InitFallbackAvaloniaTray(desktop, win, vm, appIcon);
                }
            }
            else
            {
                InitFallbackAvaloniaTray(desktop, win, vm, appIcon);
            }

            if (startMinimized)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            else
            {
                desktop.MainWindow = win;
            }

            vm.RequestAppShutdown = () =>
            {
                _isExplicitExit = true;
                desktop.Shutdown();
            };
            vm.RequestClipboardCopy = async text =>
            {
                if (win.Clipboard is not null)
                    await win.Clipboard.SetTextAsync(text);
            };
            vm.HostApprovalPromptHandler = async (clientName, remoteIp, isWriteSession) =>
            {
                return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ShowMainWindow(win);
                    var accessText = isWriteSession ? "browse & read/write access" : "browse & download access";
                    var peerLabel = string.IsNullOrWhiteSpace(clientName) ? remoteIp : $"{clientName} ({remoteIp})";
                    return await InputDialog.ShowConfirmAsync(
                        win,
                        "Allow Connection Request",
                        $"{peerLabel} wants to connect with {accessText}. Allow this connection?",
                        confirmLabel: "Allow",
                        isPrimaryConfirm: true,
                        cancelLabel: "Decline");
                });
            };
            InstanceGuard?.StartListening(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMainWindow(win)));
            desktop.ShutdownRequested += async (_, _) =>
            {
                InstanceGuard?.Dispose();
                if (OperatingSystem.IsWindows())
                    _windowsTrayIcon?.Dispose();
                _trayIcon?.Dispose();
                await vm.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitFallbackAvaloniaTray(IClassicDesktopStyleApplicationLifetime desktop, Window win, MainWindowViewModel vm, WindowIcon? appIcon)
    {
        try
        {
            var openItem = new NativeMenuItem("Open SwiftBeam");
            openItem.Click += (_, _) => ShowMainWindow(win);

            var copyPinItem = new NativeMenuItem("Copy Pairing PIN");
            copyPinItem.Click += async (_, _) =>
            {
                if (win.Clipboard is not null)
                    await win.Clipboard.SetTextAsync(vm.HostPin);
            };

            var exitItem = new NativeMenuItem("Exit SwiftBeam");
            exitItem.Click += (_, _) =>
            {
                _isExplicitExit = true;
                desktop.Shutdown();
            };

            var menu = new NativeMenu();
            menu.Items.Add(openItem);
            menu.Items.Add(copyPinItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = vm.TrayTooltipText,
                Menu = menu,
                IsVisible = true
            };
            if (appIcon is not null)
                _trayIcon.Icon = appIcon;

            _trayIcon.Clicked += (_, _) => ShowMainWindow(win);

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.TrayTooltipText) && _trayIcon is not null)
                    _trayIcon.ToolTipText = vm.TrayTooltipText;
            };
        }
        catch
        {
            // Non-fatal on headless/minimal Linux WMs without system tray support
        }
    }

    private static void ShowMainWindow(Window win)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.MainWindow is null)
        {
            d.MainWindow = win;
        }
        if (!win.IsVisible)
            win.Show();
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;
        win.Activate();

        // Briefly toggle Topmost so Windows reliably brings a restored tray window to the foreground
        if (!win.Topmost)
        {
            win.Topmost = true;
            win.Topmost = false;
        }
    }
}
