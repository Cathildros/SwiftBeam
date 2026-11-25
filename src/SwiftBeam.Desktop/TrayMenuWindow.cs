using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SwiftBeam.Core.Updates;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public sealed class TrayMenuWindow : Window
{
    private static TrayMenuWindow? _activePopup;

    private TrayMenuWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
    }

    public static void ShowAt(
        int cursorPhysicalX,
        int cursorPhysicalY,
        MainWindowViewModel vm,
        WindowIcon? appIcon,
        Action openWindowAction,
        Func<Task> copyPinAction,
        Action exitAction)
    {
        if (_activePopup is not null)
        {
            try { _activePopup.Close(); } catch { }
            _activePopup = null;
        }

        var popup = new TrayMenuWindow();
        _activePopup = popup;

        var statusDot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = vm.ServerStatusBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,8,*,8,Auto"),
            Children =
            {
                new Border
                {
                    Background = SolidColorBrush.Parse("#7C3AED"),
                    CornerRadius = new CornerRadius(7),
                    Width = 24,
                    Height = 24,
                    Child = new TextBlock
                    {
                        Text = "⇄",
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleCol.Children.Add(new TextBlock
        {
            Text = "SwiftBeam",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = SolidColorBrush.Parse("#F8FAFC")
        });
        titleCol.Children.Add(new TextBlock
        {
            Text = $"v{GitHubUpdateService.GetCurrentVersion()} • {vm.ServerStatusText}",
            FontSize = 10,
            Foreground = SolidColorBrush.Parse("#94A3B8"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(titleCol, 2);
        Grid.SetColumn(statusDot, 4);
        headerRow.Children.Add(titleCol);
        headerRow.Children.Add(statusDot);

        var copyBtn = new Button
        {
            Content = "Copy",
            Classes = { "subtle" },
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 4),
            FontSize = 11
        };
        copyBtn.Click += async (_, _) =>
        {
            popup.Close();
            await copyPinAction();
        };

        var pinCard = new Border
        {
            Background = SolidColorBrush.Parse("#0B1120"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 7),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = "PAIRING PIN", FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#64748B") },
                            new TextBlock { Text = vm.HostPinFormatted, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#A78BFA"), FontFamily = new FontFamily("Consolas, Monospace") }
                        }
                    },
                    SetCol(copyBtn, 1)
                }
            }
        };

        Button MakePillButton(string label, string bgHex, string fgHex, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Background = SolidColorBrush.Parse(bgHex),
                Foreground = SolidColorBrush.Parse(fgHex),
                BorderBrush = SolidColorBrush.Parse("#1E293B"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            b.Click += (_, _) =>
            {
                popup.Close();
                onClick();
            };
            return b;
        }

        var openBtn = MakePillButton("↗  Open SwiftBeam", "#2E1065", "#F3E8FF", openWindowAction);
        openBtn.BorderBrush = SolidColorBrush.Parse("#7C3AED");

        var shareBtn = MakePillButton(
            vm.IsServerRunning ? "⏸  Stop Sharing Files" : "▶  Start Sharing Files",
            "#0B1120",
            "#E2E8F0",
            () => { if (vm.ToggleServerCommand.CanExecute(null)) vm.ToggleServerCommand.Execute(null); });

        var webBtn = MakePillButton(
            vm.IsWebPortalRunning ? "📱  Stop Phone / Web Portal" : "📱  Start Phone / Web Portal",
            "#0B1120",
            "#C4B5FD",
            () => { if (vm.ToggleWebPortalCommand.CanExecute(null)) vm.ToggleWebPortalCommand.Execute(null); });

        var exitBtn = MakePillButton("✕  Exit SwiftBeam", "#2A1215", "#FCA5A5", exitAction);
        exitBtn.BorderBrush = SolidColorBrush.Parse("#7F1D1D");

        popup.Content = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#334155"),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            Width = 258,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 8,
                Blur = 24,
                Color = Color.FromArgb(160, 0, 0, 0)
            }),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    headerRow,
                    pinCard,
                    openBtn,
                    shareBtn,
                    webBtn,
                    new Border { Height = 1, Background = SolidColorBrush.Parse("#1E293B"), Margin = new Thickness(4, 2) },
                    exitBtn
                }
            }
        };

        popup.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                popup.Close();
        };
        popup.Deactivated += (_, _) =>
        {
            try { popup.Close(); } catch { }
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activePopup, popup))
                _activePopup = null;
        };

        // Measure and clamp nicely above the cursor / taskbar inside the active screen
        popup.Opened += (_, _) =>
        {
            double scale = popup.DesktopScaling > 0 ? popup.DesktopScaling : 1.0;
            int wPx = (int)Math.Round(popup.Bounds.Width * scale);
            int hPx = (int)Math.Round(popup.Bounds.Height * scale);
            if (wPx <= 0) wPx = (int)Math.Round(258 * scale);
            if (hPx <= 0) hPx = (int)Math.Round(305 * scale);

            var screen = popup.Screens.ScreenFromPoint(new PixelPoint(cursorPhysicalX, cursorPhysicalY))
                         ?? popup.Screens.Primary;
            var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

            int targetX = cursorPhysicalX - wPx / 2;
            int targetY = cursorPhysicalY - hPx - 10;

            if (targetY < work.Y + 8)
                targetY = cursorPhysicalY + 10;
            targetX = Math.Clamp(targetX, work.X + 8, Math.Max(work.X + 8, work.Right - wPx - 8));
            targetY = Math.Clamp(targetY, work.Y + 8, Math.Max(work.Y + 8, work.Bottom - hPx - 8));

            popup.Position = new PixelPoint(targetX, targetY);
            popup.Activate();
        };

        popup.Show();
    }

    private static T SetCol<T>(T control, int col) where T : Control
    {
        Grid.SetColumn(control, col);
        return control;
    }
}
