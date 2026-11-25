using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Server;
using SwiftBeam.Core.Settings;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public static class AutoSyncPairDialog
{
    private sealed record PeerPresetChoice(
        string Label,
        string DeviceName,
        string Host,
        int Port,
        bool UseTls,
        string Pin,
        bool IsCustom);

    public static async Task<AutoSyncPairConfig?> ShowEditAsync(
        Window owner,
        MainWindowViewModel vm,
        AutoSyncPairConfig initial,
        bool isNew)
    {
        var dialog = new Window
        {
            Title = isNew ? "New Live Auto-Sync Folder Pair" : "Edit Live Auto-Sync Folder Pair",
            Width = 620,
            Height = 650,
            MinWidth = 520,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#0B1120"),
            Foreground = SolidColorBrush.Parse("#F1F5F9")
        };

        var presets = new List<PeerPresetChoice>();
        var connected = vm.GetCurrentConnectedPeerSnapshot();
        if (connected is { } conn)
        {
            presets.Add(new PeerPresetChoice(
                $"{conn.DeviceName} ({conn.Host}) — Connected now",
                conn.DeviceName,
                conn.Host,
                conn.Port,
                conn.UseTls,
                conn.Pin,
                false));
        }

        foreach (var peer in vm.DiscoveredPeers)
        {
            if (presets.Any(p => p.DeviceName.Equals(peer.DeviceName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var ep = peer.Endpoints.FirstOrDefault();
            if (ep is null) continue;
            string savedPin = vm.SavedPeers.FirstOrDefault(s =>
                s.DeviceName.Equals(peer.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                s.Host.Equals(ep.Host, StringComparison.OrdinalIgnoreCase))?.SavedPin ?? string.Empty;
            presets.Add(new PeerPresetChoice(
                $"{peer.DeviceName} ({ep.Host}) — Nearby",
                peer.DeviceName,
                ep.Host,
                ep.Port,
                true,
                savedPin,
                false));
        }

        foreach (var saved in vm.SavedPeers)
        {
            if (presets.Any(p => p.Host.Equals(saved.Host, StringComparison.OrdinalIgnoreCase) && p.Port == saved.Port))
                continue;
            presets.Add(new PeerPresetChoice(
                string.IsNullOrWhiteSpace(saved.DeviceName) ? $"{saved.Host}:{saved.Port} — Saved" : $"{saved.DeviceName} ({saved.Host}) — Saved",
                saved.DeviceName,
                saved.Host,
                saved.Port,
                saved.UseTls,
                saved.SavedPin,
                false));
        }

        presets.Add(new PeerPresetChoice("Custom / Enter IP manually", initial.PeerDeviceName, initial.PeerHost, initial.PeerPort, initial.PeerUseTls, initial.PeerPin, true));

        int initialPresetIdx = presets.FindIndex(p => !p.IsCustom &&
            ((!string.IsNullOrWhiteSpace(initial.PeerDeviceName) && p.DeviceName.Equals(initial.PeerDeviceName, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(initial.PeerHost) && p.Host.Equals(initial.PeerHost, StringComparison.OrdinalIgnoreCase))));
        if (initialPresetIdx < 0)
            initialPresetIdx = presets.Count - 1;

        var nameBox = new TextBox { Classes = { "pill" }, Text = initial.Name, Watermark = "e.g. Work Projects Sync" };
        var localBox = new TextBox { Classes = { "pill" }, Text = initial.LocalPath, Watermark = "Select local folder..." };
        var browseLocalBtn = new Button { Content = "Browse...", Classes = { "subtle" }, CornerRadius = new CornerRadius(8), MinHeight = 34, Padding = new Thickness(12, 5) };

        var peerCombo = new ComboBox
        {
            Classes = { "pill" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = initialPresetIdx
        };
        foreach (var p in presets)
            peerCombo.Items.Add(new ComboBoxItem { Content = p.Label });

        var hostBox = new TextBox { Classes = { "pill" }, Text = initial.PeerHost, Watermark = "IP or hostname" };
        var portBox = new TextBox { Classes = { "pill" }, Text = (initial.PeerPort > 0 ? initial.PeerPort : ProtocolConstants.DefaultTransferPort).ToString(), Watermark = "45890" };
        var pinBox = new TextBox { Classes = { "pill" }, Text = initial.PeerPin, Watermark = "6-digit PIN" };
        var tlsCheck = new CheckBox { IsChecked = initial.PeerUseTls, Content = "TLS 1.3", VerticalAlignment = VerticalAlignment.Center };

        string selectedDeviceName = initial.PeerDeviceName;
        peerCombo.SelectionChanged += (_, _) =>
        {
            if (peerCombo.SelectedIndex >= 0 && peerCombo.SelectedIndex < presets.Count)
            {
                var choice = presets[peerCombo.SelectedIndex];
                if (!choice.IsCustom)
                {
                    selectedDeviceName = choice.DeviceName;
                    hostBox.Text = choice.Host;
                    portBox.Text = choice.Port.ToString();
                    tlsCheck.IsChecked = choice.UseTls;
                    if (!string.IsNullOrWhiteSpace(choice.Pin))
                        pinBox.Text = choice.Pin;
                }
            }
        };

        var remoteBox = new TextBox { Classes = { "pill" }, Text = initial.RemotePath, Watermark = "e.g. C:/Users/Name/SyncFolder (leave empty for restricted host root)" };

        var modeCombo = new ComboBox
        {
            Classes = { "pill" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new ComboBoxItem { Content = "Two-Way — Mirror additions, edits & deletes on both computers" },
                new ComboBoxItem { Content = "Send Only — Mirror this PC → Remote PC" },
                new ComboBoxItem { Content = "Receive Only — Mirror Remote PC → This PC" }
            },
            SelectedIndex = (int)initial.Mode
        };

        var syncDeletesCheck = new CheckBox { IsChecked = initial.SyncDeletes, Content = "Sync deletions (removing a file/folder on one PC removes it on the other)" };
        var softTrashCheck = new CheckBox { IsChecked = initial.UseSoftTrash, Content = "Move deleted local files into .swiftbeam-trash before deleting (recommended)" };
        var includeHiddenCheck = new CheckBox { IsChecked = initial.IncludeHidden, Content = "Include hidden files and folders" };
        var enabledCheck = new CheckBox { IsChecked = initial.Enabled, Content = "Enable live auto-sync in the background" };

        var pollBox = new NumericUpDown
        {
            Classes = { "pill" },
            Minimum = 2,
            Maximum = 300,
            FormatString = "0",
            Value = Math.Clamp(initial.PollIntervalSeconds, 2, 300)
        };
        var ignoreBox = new TextBox
        {
            Classes = { "pill" },
            Text = initial.IgnorePatterns,
            Watermark = "*.tmp;*.part;.DS_Store;Thumbs.db"
        };

        var errorText = new TextBlock
        {
            Foreground = SolidColorBrush.Parse("#F87171"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };

        browseLocalBtn.Click += async (_, _) =>
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Local Folder to Auto-Sync",
                AllowMultiple = false
            });
            string? picked = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                localBox.Text = picked;
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                    nameBox.Text = Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        };

        AutoSyncPairConfig? result = null;
        var cancelBtn = new Button { Content = "Cancel", Classes = { "subtle" }, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7) };
        var saveBtn = new Button { Content = isNew ? "Create Sync Pair" : "Save Changes", Classes = { "primary" }, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 7) };

        cancelBtn.Click += (_, _) => dialog.Close();
        saveBtn.Click += (_, _) =>
        {
            string localPath = (localBox.Text ?? string.Empty).Trim();
            string remotePath = (remoteBox.Text ?? string.Empty).Trim();
            string host = (hostBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(localPath))
            {
                errorText.Text = "Please choose a local folder to sync.";
                errorText.IsVisible = true;
                return;
            }
            if (remotePath == FileShareServer.DrivesVirtualPath)
            {
                errorText.Text = "Remote folder cannot be the top-level Drives list — pick a specific folder path.";
                errorText.IsVisible = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                errorText.Text = "Select a paired computer or enter its IP/hostname.";
                errorText.IsVisible = true;
                return;
            }
            if (!int.TryParse((portBox.Text ?? string.Empty).Trim(), out int port) || port is < 1024 or > 65535)
            {
                errorText.Text = "Enter a valid port between 1024 and 65535.";
                errorText.IsVisible = true;
                return;
            }

            try
            {
                Directory.CreateDirectory(localPath);
            }
            catch (Exception ex)
            {
                errorText.Text = $"Cannot access or create local folder: {ex.Message}";
                errorText.IsVisible = true;
                return;
            }

            string pairName = (nameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pairName))
                pairName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            result = new AutoSyncPairConfig
            {
                Id = string.IsNullOrWhiteSpace(initial.Id) ? Guid.NewGuid().ToString("N")[..12] : initial.Id,
                Name = pairName,
                LocalPath = Path.GetFullPath(localPath),
                RemotePath = remotePath,
                PeerDeviceName = selectedDeviceName,
                PeerHost = host,
                PeerPort = port,
                PeerUseTls = tlsCheck.IsChecked == true,
                PeerPin = (pinBox.Text ?? string.Empty).Trim(),
                Mode = (AutoSyncMode)Math.Clamp(modeCombo.SelectedIndex, 0, 2),
                Enabled = enabledCheck.IsChecked == true,
                SyncDeletes = syncDeletesCheck.IsChecked == true,
                UseSoftTrash = softTrashCheck.IsChecked == true,
                IncludeHidden = includeHiddenCheck.IsChecked == true,
                PollIntervalSeconds = Math.Clamp((int)(pollBox.Value ?? 5), 2, 300),
                IgnorePatterns = (ignoreBox.Text ?? string.Empty).Trim()
            };
            dialog.Close();
        };

        var header = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = dialog.Title, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#F8FAFC") },
                    new TextBlock { Text = "Real-time file watcher + automatic remote polling with safe deletion tracking.", FontSize = 11.5, Foreground = SolidColorBrush.Parse("#94A3B8") }
                }
            }
        };

        var body = new ScrollViewer
        {
            Padding = new Thickness(16, 12),
            Content = new StackPanel
            {
                Spacing = 11,
                Children =
                {
                    Labeled("Pair Name", nameBox),
                    Labeled("Local Folder", new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,8,Auto"),
                        Children = { localBox, SetCol(browseLocalBtn, 2) }
                    }),
                    Labeled("Target Computer", peerCombo),
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,8,86,8,118,10,Auto"),
                        Children =
                        {
                            Labeled("Host / IP", hostBox),
                            SetCol(Labeled("Port", portBox), 2),
                            SetCol(Labeled("PIN", pinBox), 4),
                            SetCol(new StackPanel { Margin = new Thickness(0, 20, 0, 0), Children = { tlsCheck } }, 6)
                        }
                    },
                    Labeled("Remote Folder Path", remoteBox),
                    Labeled("Sync Direction", modeCombo),
                    syncDeletesCheck,
                    softTrashCheck,
                    includeHiddenCheck,
                    enabledCheck,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("140,10,*"),
                        Children =
                        {
                            Labeled("Check Interval (sec)", pollBox),
                            SetCol(Labeled("Ignore Patterns", ignoreBox), 2)
                        }
                    },
                    errorText
                }
            }
        };

        var footer = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancelBtn, saveBtn }
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(footer);

        dialog.Content = root;
        await dialog.ShowDialog(owner);
        return result;
    }

    private static StackPanel Labeled(string label, Control control) => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, FontSize = 11.5, Foreground = SolidColorBrush.Parse("#94A3B8") },
            control
        }
    };

    private static T SetCol<T>(T control, int col) where T : Control
    {
        Grid.SetColumn(control, col);
        return control;
    }
}
