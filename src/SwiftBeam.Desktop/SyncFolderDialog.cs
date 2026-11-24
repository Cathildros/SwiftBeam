using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SwiftBeam.Core.Client;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public static class SyncFolderDialog
{
    private sealed class SyncRowItem : ObservableObject
    {
        private bool _isChecked = true;
        public SyncDiffItem Model { get; }
        public SyncRowItem(SyncDiffItem model) => Model = model;

        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        public string ActionBadge => Model.Action == SyncActionKind.CopyToRemote ? "SEND →" : "← PULL";
        public IBrush ActionBrush => Model.Action == SyncActionKind.CopyToRemote
            ? SolidColorBrush.Parse("#34D399")
            : SolidColorBrush.Parse("#A78BFA");
        public string PathText => Model.RelativePath;
        public string SizeText => TransferManager.FormatBytes(Model.SizeBytes);
        public string ReasonText => Model.Reason;
    }

    public static async Task ShowSyncAsync(Window owner, MainWindowViewModel vm)
    {
        var dialog = new Window
        {
            Title = "Folder Diff & Smart Sync",
            Width = 780,
            Height = 560,
            MinWidth = 560,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#0B1120"),
            Foreground = SolidColorBrush.Parse("#F1F5F9")
        };

        var dirCombo = new ComboBox
        {
            Classes = { "pill" },
            MinWidth = 220,
            Items =
            {
                new ComboBoxItem { Content = vm.IsRemoteReadOnly ? "Local → Remote (Read-Only blocked)" : "Local → Remote (Send newer/missing)" },
                new ComboBoxItem { Content = "Remote → Local (Pull newer/missing)" },
                new ComboBoxItem { Content = vm.IsRemoteReadOnly ? "Remote → Local only (Remote Read-Only)" : "Two-Way (Newest file wins)" }
            },
            SelectedIndex = vm.IsRemoteReadOnly ? 1 : 2
        };

        var statusText = new TextBlock
        {
            Text = "Click Scan to compare the open Local folder and Remote folder.",
            FontSize = 12,
            Foreground = SolidColorBrush.Parse("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var rowsPanel = new StackPanel { Spacing = 4 };
        var currentRows = new List<SyncRowItem>();

        var scanBtn = new Button
        {
            Content = "Scan Differences",
            Classes = { "subtle" },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6)
        };

        var syncBtn = new Button
        {
            Content = "Start Sync",
            Classes = { "primary" },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 6),
            IsEnabled = false
        };

        void UpdateSummaryAndButton()
        {
            int checkedSend = currentRows.Count(r => r.IsChecked && r.Model.Action == SyncActionKind.CopyToRemote);
            int checkedPull = currentRows.Count(r => r.IsChecked && r.Model.Action == SyncActionKind.CopyToLocal);
            int totalChecked = checkedSend + checkedPull;
            long totalBytes = currentRows.Where(r => r.IsChecked).Sum(r => r.Model.SizeBytes);

            syncBtn.IsEnabled = totalChecked > 0;
            syncBtn.Content = totalChecked > 0
                ? $"Start Sync ({totalChecked} files • {TransferManager.FormatBytes(totalBytes)})"
                : "Start Sync";
        }

        async Task RunScanAsync()
        {
            scanBtn.IsEnabled = false;
            syncBtn.IsEnabled = false;
            statusText.Text = "Scanning remote folder recursively & comparing timestamps...";
            rowsPanel.Children.Clear();
            currentRows.Clear();

            try
            {
                var direction = dirCombo.SelectedIndex switch
                {
                    0 => SyncDirection.LocalToRemote,
                    1 => SyncDirection.RemoteToLocal,
                    _ => vm.IsRemoteReadOnly ? SyncDirection.RemoteToLocal : SyncDirection.TwoWayNewest
                };

                var plan = await vm.PlanFolderSyncAsync(direction);
                if (plan.Items.Count == 0)
                {
                    statusText.Text = $"Folders match cleanly ({plan.IdenticalCount} identical file(s), 0 differences).";
                }
                else
                {
                    statusText.Text = $"Found {plan.Items.Count} difference(s) ({plan.ToSendCount} send, {plan.ToPullCount} pull, {plan.IdenticalCount} identical).";
                    foreach (var diff in plan.Items)
                    {
                        var row = new SyncRowItem(diff);
                        row.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(SyncRowItem.IsChecked))
                                UpdateSummaryAndButton();
                        };
                        currentRows.Add(row);

                        var cb = new CheckBox
                        {
                            Classes = { "compact" },
                            IsChecked = true,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        cb.IsCheckedChanged += (_, _) => row.IsChecked = cb.IsChecked == true;

                        var rowBorder = new Border
                        {
                            Background = SolidColorBrush.Parse("#111827"),
                            BorderBrush = SolidColorBrush.Parse("#1F2937"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(10, 6),
                            Child = new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("24,68,*,90,150"),
                                Children =
                                {
                                    cb,
                                    SetCol(new TextBlock { Text = row.ActionBadge, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = row.ActionBrush, VerticalAlignment = VerticalAlignment.Center }, 1),
                                    SetCol(new TextBlock { Text = row.PathText, FontSize = 12, Foreground = SolidColorBrush.Parse("#F1F5F9"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) }, 2),
                                    SetCol(new TextBlock { Text = row.SizeText, FontSize = 11, Foreground = SolidColorBrush.Parse("#94A3B8"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 3),
                                    SetCol(new TextBlock { Text = row.ReasonText, FontSize = 11, Foreground = SolidColorBrush.Parse("#64748B"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 4)
                                }
                            }
                        };
                        rowsPanel.Children.Add(rowBorder);
                    }
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Scan failed: {ex.Message}";
            }
            finally
            {
                scanBtn.IsEnabled = true;
                UpdateSummaryAndButton();
            }
        }

        scanBtn.Click += async (_, _) => await RunScanAsync();
        syncBtn.Click += async (_, _) =>
        {
            var selected = currentRows.Where(r => r.IsChecked).Select(r => r.Model).ToList();
            if (selected.Count > 0)
                await vm.ExecuteSyncDiffAsync(selected);
            dialog.Close();
        };

        var cancelBtn = new Button
        {
            Content = "Close",
            Classes = { "subtle" },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 6),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        var topCard = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,12,*"),
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock { Text = "LOCAL FOLDER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#64748B") },
                                    new TextBlock { Text = vm.LocalPathInput, FontFamily = new FontFamily("Consolas, Monospace"), FontSize = 11.5, Foreground = SolidColorBrush.Parse("#E2E8F0"), TextTrimming = TextTrimming.CharacterEllipsis }
                                }
                            },
                            SetCol(new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock { Text = "REMOTE FOLDER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#64748B") },
                                    new TextBlock { Text = vm.RemotePathInput, FontFamily = new FontFamily("Consolas, Monospace"), FontSize = 11.5, Foreground = SolidColorBrush.Parse("#A78BFA"), TextTrimming = TextTrimming.CharacterEllipsis }
                                }
                            }, 2)
                        }
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,10,*,Auto"),
                        Children =
                        {
                            new TextBlock { Text = "Sync Mode:", FontSize = 12, Foreground = SolidColorBrush.Parse("#94A3B8"), VerticalAlignment = VerticalAlignment.Center },
                            SetCol(dirCombo, 2),
                            SetCol(scanBtn, 3)
                        }
                    }
                }
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(topCard, 0);
        root.Children.Add(topCard);

        var bodyScroll = new ScrollViewer
        {
            Margin = new Thickness(14, 10),
            Content = rowsPanel
        };
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);

        var footer = new Border
        {
            Background = SolidColorBrush.Parse("#0F172A"),
            BorderBrush = SolidColorBrush.Parse("#1E293B"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    statusText,
                    SetCol(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { cancelBtn, syncBtn }
                    }, 1)
                }
            }
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        dialog.Content = root;
        dialog.Opened += async (_, _) => await RunScanAsync();
        await dialog.ShowDialog(owner);
    }

    private static T SetCol<T>(T control, int col) where T : Control
    {
        Grid.SetColumn(control, col);
        return control;
    }
}
