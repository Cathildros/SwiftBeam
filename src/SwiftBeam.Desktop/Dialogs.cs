using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SwiftBeam.Desktop;

public static class InputDialog
{
    public static async Task<string?> ShowPromptAsync(Window owner, string title, string prompt, string initialValue = "")
    {
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 165,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#0F172A"),
            Foreground = SolidColorBrush.Parse("#F1F5F9")
        };

        var box = new TextBox
        {
            Text = initialValue,
            Classes = { "pill" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var okBtn = new Button
        {
            Content = "OK",
            Classes = { "primary" },
            Padding = new Thickness(18, 6),
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Classes = { "subtle" },
            Padding = new Thickness(14, 6),
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        okBtn.Click += (_, _) => { result = box.Text?.Trim(); dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = null; dialog.Close(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Text?.Trim(); dialog.Close(); }
            else if (e.Key == Key.Escape) { result = null; dialog.Close(); }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = prompt, FontSize = 13, Foreground = SolidColorBrush.Parse("#CBD5E1"), TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, okBtn }
                }
            }
        };

        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static async Task<bool> ShowConfirmAsync(Window owner, string title, string message, string confirmLabel = "Delete")
    {
        bool confirmed = false;

        var dialog = new Window
        {
            Title = title,
            Width = 410,
            Height = 155,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#0F172A"),
            Foreground = SolidColorBrush.Parse("#F1F5F9")
        };

        var yesBtn = new Button
        {
            Content = confirmLabel,
            Classes = { "danger" },
            Padding = new Thickness(18, 6),
            MinWidth = 86,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var noBtn = new Button
        {
            Content = "Cancel",
            Classes = { "subtle" },
            Padding = new Thickness(14, 6),
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        yesBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };
        noBtn.Click += (_, _) => { confirmed = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, FontSize = 13, Foreground = SolidColorBrush.Parse("#E2E8F0"), TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { noBtn, yesBtn }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }

    /// <summary>
    /// Generates a crisp 32x32 sky-blue rounded SwiftBeam icon in-memory for both Window and System Tray (Windows &amp; Linux).
    /// </summary>
    public static WindowIcon CreateAppIcon()
    {
        const int size = 32;
        var wb = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            unsafe
            {
                byte* ptr = (byte*)fb.Address;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int idx = (y * size + x) * 4;
                        // Rounded square mask (radius 7)
                        int dx = Math.Max(0, Math.Max(7 - x, x - (size - 8)));
                        int dy = Math.Max(0, Math.Max(7 - y, y - (size - 8)));
                        bool inside = (dx * dx + dy * dy) <= 49;

                        if (!inside)
                        {
                            ptr[idx] = 0; ptr[idx + 1] = 0; ptr[idx + 2] = 0; ptr[idx + 3] = 0;
                            continue;
                        }

                        // Draw clean white opposing transfer arrows inside purple tile (#8B5CF6)
                        bool topArrow = (y >= 9 && y <= 12 && x >= 7 && x <= 23) ||
                                        (x >= 18 && x <= 24 && Math.Abs(y - 10.5) <= (24 - x) * 0.7);
                        bool botArrow = (y >= 19 && y <= 22 && x >= 8 && x <= 24) ||
                                        (x >= 7 && x <= 13 && Math.Abs(y - 20.5) <= (x - 7) * 0.7);

                        if (topArrow || botArrow)
                        {
                            ptr[idx] = 255; ptr[idx + 1] = 255; ptr[idx + 2] = 255; ptr[idx + 3] = 255;
                        }
                        else
                        {
                            // #8B5CF6 in BGRA
                            ptr[idx] = 0xF6; ptr[idx + 1] = 0x5C; ptr[idx + 2] = 0x8B; ptr[idx + 3] = 255;
                        }
                    }
                }
            }
        }

        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
