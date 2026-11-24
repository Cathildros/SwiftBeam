using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SwiftBeam.Core.Client;

namespace SwiftBeam.Desktop;

public static class PreviewDialog
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"
    };

    public static bool IsImageFile(string fileName) =>
        ImageExtensions.Contains(Path.GetExtension(fileName));

    public static async Task ShowPreviewAsync(
        Window owner,
        string fileName,
        string subtitle,
        byte[] data,
        long totalFileSize,
        bool truncated,
        Func<string, Task>? copyTextAsync)
    {
        var dialog = new Window
        {
            Title = $"Quick Preview — {fileName}",
            Width = 760,
            Height = 560,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#0B1120"),
            Foreground = SolidColorBrush.Parse("#F1F5F9")
        };

        var header = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10)
        };

        string sizeNote = truncated
            ? $"{TransferManager.FormatBytes(totalFileSize)} (showing first {TransferManager.FormatBytes(data.Length)})"
            : TransferManager.FormatBytes(totalFileSize);

        header.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = fileName, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#F8FAFC") },
                        new TextBlock { Text = $"{subtitle} • {sizeNote}", FontSize = 11, Foreground = SolidColorBrush.Parse("#94A3B8"), TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                }
            }
        };

        Control bodyControl;
        string? textToCopy = null;

        if (!truncated && IsImageFile(fileName) && TryDecodeImage(data, out var bmp) && bmp is not null)
        {
            bodyControl = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 700,
                    MaxHeight = 420,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            dialog.Closed += (_, _) => bmp.Dispose();
        }
        else if (LooksLikeText(data))
        {
            string text = DecodeUtf8Text(data);
            textToCopy = text;
            bodyControl = new Border
            {
                Background = SolidColorBrush.Parse("#0F172A"),
                BorderBrush = SolidColorBrush.Parse("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock
                    {
                        Text = text,
                        FontFamily = new FontFamily("Consolas, Monospace"),
                        FontSize = 12,
                        Foreground = SolidColorBrush.Parse("#E2E8F0"),
                        TextWrapping = TextWrapping.NoWrap
                    }
                }
            };
        }
        else
        {
            bodyControl = new Border
            {
                Background = SolidColorBrush.Parse("#0F172A"),
                BorderBrush = SolidColorBrush.Parse("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Binary file preview unavailable", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = SolidColorBrush.Parse("#C4B5FD"), HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{fileName} ({TransferManager.FormatBytes(totalFileSize)})", FontSize = 12, Foreground = SolidColorBrush.Parse("#94A3B8"), HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "Download or Send this file to open it in its native application.", FontSize = 11.5, Foreground = SolidColorBrush.Parse("#64748B"), HorizontalAlignment = HorizontalAlignment.Center }
                    }
                }
            };
        }

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (textToCopy is not null && copyTextAsync is not null)
        {
            var copyBtn = new Button
            {
                Content = "Copy Text",
                Classes = { "subtle" },
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 6)
            };
            copyBtn.Click += async (_, _) =>
            {
                await copyTextAsync(textToCopy);
                copyBtn.Content = "Copied!";
            };
            footerButtons.Children.Add(copyBtn);
        }

        var closeBtn = new Button
        {
            Content = "Close",
            Classes = { "primary" },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 6),
            IsDefault = true,
            IsCancel = true
        };
        closeBtn.Click += (_, _) => dialog.Close();
        footerButtons.Children.Add(closeBtn);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var contentWrap = new Border { Padding = new Thickness(14), Child = bodyControl };
        Grid.SetRow(contentWrap, 1);
        root.Children.Add(contentWrap);

        var footerWrap = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = footerButtons
        };
        Grid.SetRow(footerWrap, 2);
        root.Children.Add(footerWrap);

        dialog.Content = root;
        await dialog.ShowDialog(owner);
    }

    private static bool TryDecodeImage(byte[] data, out Bitmap? bitmap)
    {
        bitmap = null;
        if (data.Length == 0) return false;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            bitmap = new Bitmap(ms);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeText(byte[] data)
    {
        if (data.Length == 0) return true;
        int checkLen = Math.Min(data.Length, 4096);
        int controlCount = 0;
        for (int i = 0; i < checkLen; i++)
        {
            byte b = data[i];
            if (b == 0) return false;
            if (b < 0x09 || (b is > 0x0D and < 0x20))
                controlCount++;
        }
        return controlCount <= checkLen * 0.02;
    }

    private static string DecodeUtf8Text(byte[] data)
    {
        if (data.Length == 0) return "(empty file)";
        return Encoding.UTF8.GetString(data);
    }
}
