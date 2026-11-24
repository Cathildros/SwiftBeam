using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Settings;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public partial class MainWindow : Window
{
    private const string LocalDragFormat = "SwiftBeam.LocalEntries";
    private const string RemoteDragFormat = "SwiftBeam.RemoteEntries";

    private GridLength _savedTransferPanelHeight = new(210, GridUnitType.Pixel);

    private RemoteEntryViewModel? _lastLocalCheckAnchor;
    private RemoteEntryViewModel? _lastRemoteCheckAnchor;
    private ListBox? _dragSourceList;
    private RemoteEntryViewModel? _dragEntry;
    private Point _dragStartPoint;
    private bool _isDragging;

    public MainWindow()
    {
        InitializeComponent();

        var localDropZone = this.FindControl<Grid>("LocalDropZone");
        if (localDropZone is not null)
        {
            localDropZone.AddHandler(DragDrop.DragOverEvent, OnLocalDragOver);
            localDropZone.AddHandler(DragDrop.DropEvent, OnLocalDrop);
        }

        var remoteDropZone = this.FindControl<Grid>("RemoteDropZone");
        if (remoteDropZone is not null)
        {
            remoteDropZone.AddHandler(DragDrop.DragOverEvent, OnRemoteDragOver);
            remoteDropZone.AddHandler(DragDrop.DropEvent, OnRemoteDrop);
        }

        var localList = this.FindControl<ListBox>("LocalFileList");
        if (localList is not null)
        {
            localList.AddHandler(PointerPressedEvent, OnFileListPointerPressed, RoutingStrategies.Tunnel);
            localList.AddHandler(PointerMovedEvent, OnFileListPointerMoved, RoutingStrategies.Tunnel);
            localList.AddHandler(PointerReleasedEvent, OnFileListPointerReleased, RoutingStrategies.Tunnel);
        }

        var remoteList = this.FindControl<ListBox>("RemoteFileList");
        if (remoteList is not null)
        {
            remoteList.AddHandler(PointerPressedEvent, OnFileListPointerPressed, RoutingStrategies.Tunnel);
            remoteList.AddHandler(PointerMovedEvent, OnFileListPointerMoved, RoutingStrategies.Tunnel);
            remoteList.AddHandler(PointerReleasedEvent, OnFileListPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnToggleTransferPanelClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var grid = this.FindControl<Grid>("TransferWorkspaceGrid");
        if (grid is null || grid.RowDefinitions.Count < 3) return;

        if (ViewModel.IsTransferPanelExpanded)
        {
            if (grid.RowDefinitions[2].Height.IsAbsolute && grid.RowDefinitions[2].Height.Value >= 90)
                _savedTransferPanelHeight = grid.RowDefinitions[2].Height;

            ViewModel.IsTransferPanelExpanded = false;
            grid.RowDefinitions[1].Height = new GridLength(6, GridUnitType.Pixel);
            grid.RowDefinitions[2].Height = GridLength.Auto;
        }
        else
        {
            ViewModel.IsTransferPanelExpanded = true;
            grid.RowDefinitions[1].Height = new GridLength(6, GridUnitType.Pixel);
            grid.RowDefinitions[2].Height = _savedTransferPanelHeight;
        }
    }

    // --- Shifting Top-Bar Connect & Host Flyouts ---
    private Flyout? GetConnectFlyout()
    {
        var anchor = this.FindControl<Border>("ConnectPillBorder");
        return anchor is null ? null : FlyoutBase.GetAttachedFlyout(anchor) as Flyout;
    }

    private Flyout? GetHostMenuFlyout()
    {
        var anchor = this.FindControl<Border>("HostPillBorder");
        return anchor is null ? null : FlyoutBase.GetAttachedFlyout(anchor) as Flyout;
    }

    private void OpenConnectFlyout(ConnectPopoverStep step)
    {
        if (ViewModel is null) return;
        ViewModel.ConnectStep = step;

        var anchor = this.FindControl<Border>("ConnectPillBorder");
        var flyout = anchor is null ? null : FlyoutBase.GetAttachedFlyout(anchor) as Flyout;
        if (anchor is not null && flyout is not null && !flyout.IsOpen)
            flyout.ShowAt(anchor);

        FocusFlyoutStepInput(step);
    }

    private void HideConnectFlyout() => GetConnectFlyout()?.Hide();

    private void FocusFlyoutStepInput(ConnectPopoverStep step)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string? targetName = step switch
            {
                ConnectPopoverStep.EnterPin => "FlyoutPinBox",
                ConnectPopoverStep.ManualEntry => "FlyoutHostBox",
                _ => null
            };
            if (targetName is null) return;

            var box = this.FindControl<TextBox>(targetName);
            if (box is not null && box.IsEffectivelyVisible)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private async Task AttemptDirectOrPromptPinAsync()
    {
        if (ViewModel is null) return;

        if (string.IsNullOrWhiteSpace(ViewModel.ConnectHost))
        {
            OpenConnectFlyout(ConnectPopoverStep.PickTarget);
            return;
        }

        if (!ViewModel.CurrentTargetRequiresPin() || ViewModel.CurrentTargetHasSavedPin())
        {
            var res = await ViewModel.TryConnectAsync();
            if (res.Success)
            {
                HideConnectFlyout();
            }
            else if (res.IsAuthError)
            {
                ViewModel.ConnectPin = string.Empty;
                ViewModel.SetConnectionError("Saved PIN was rejected — enter the new 6-digit PIN.");
                OpenConnectFlyout(ConnectPopoverStep.EnterPin);
            }
        }
        else
        {
            ViewModel.ConnectPin = string.Empty;
            ViewModel.SetConnectionError(string.Empty);
            OpenConnectFlyout(ConnectPopoverStep.EnterPin);
        }
    }

    private void OnOpenConnectPickerClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetConnectionError(string.Empty);
        OpenConnectFlyout(ConnectPopoverStep.PickTarget);
    }

    private async void OnSmartConnectClick(object? sender, RoutedEventArgs e)
    {
        await AttemptDirectOrPromptPinAsync();
    }

    private async void OnFlyoutNearbyPeerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Control { DataContext: PeerItemViewModel peer }) return;
        ViewModel.SelectNearbyPeerForConnect(peer);
        await AttemptDirectOrPromptPinAsync();
    }

    private async void OnFlyoutSavedPeerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Control { DataContext: SavedPeerEntry saved }) return;
        ViewModel.SelectSavedPeerForConnect(saved);
        await AttemptDirectOrPromptPinAsync();
    }

    private void OnFlyoutGoManualClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetConnectionError(string.Empty);
        OpenConnectFlyout(ConnectPopoverStep.ManualEntry);
    }

    private void OnFlyoutBackToPickClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SetConnectionError(string.Empty);
        ViewModel.ConnectStep = ConnectPopoverStep.PickTarget;
    }

    private async void OnFlyoutManualContinueClick(object? sender, RoutedEventArgs e)
    {
        await ContinueManualEntryAsync();
    }

    private async void OnFlyoutManualKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ContinueManualEntryAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideConnectFlyout();
        }
    }

    private async Task ContinueManualEntryAsync()
    {
        if (ViewModel is null) return;
        string host = ViewModel.ConnectHost.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ViewModel.SetConnectionError("Enter a remote IP address or hostname.");
            FocusFlyoutStepInput(ConnectPopoverStep.ManualEntry);
            return;
        }

        if (!int.TryParse(ViewModel.ConnectPort.Trim(), out int port) || port is < 1 or > 65535)
        {
            ViewModel.SetConnectionError($"Enter a valid port (1–65535, default {ProtocolConstants.DefaultTransferPort}).");
            return;
        }

        ViewModel.PrepareManualTarget();
        await AttemptDirectOrPromptPinAsync();
    }

    private async void OnFlyoutPinConnectClick(object? sender, RoutedEventArgs e)
    {
        await SubmitPinConnectAsync();
    }

    private async void OnFlyoutPinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitPinConnectAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideConnectFlyout();
        }
    }

    private async Task SubmitPinConnectAsync()
    {
        if (ViewModel is null) return;
        var res = await ViewModel.TryConnectAsync();
        if (res.Success)
            HideConnectFlyout();
        else
            FocusFlyoutStepInput(ConnectPopoverStep.EnterPin);
    }

    private void OnFlyoutCancelClick(object? sender, RoutedEventArgs e) => HideConnectFlyout();

    private void OnOpenHostMenuClick(object? sender, RoutedEventArgs e)
    {
        var anchor = this.FindControl<Border>("HostPillBorder");
        var flyout = GetHostMenuFlyout();
        if (anchor is not null && flyout is not null)
            flyout.ShowAt(anchor);
    }

    private void OnOpenHostSettingsFromMenuClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.ActiveTabIndex = 1;
        GetHostMenuFlyout()?.Hide();
    }

    // --- Column Sorting ---
    private void OnSortLocalClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is Button { Tag: string col })
            ViewModel.SortLocal(col);
    }

    private void OnSortRemoteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is Button { Tag: string col })
            ViewModel.SortRemote(col);
    }

    // --- Local File Actions ---
    private async void OnLocalNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string? name = await InputDialog.ShowPromptAsync(this, "New Local Folder", "Enter new folder name:");
        if (!string.IsNullOrWhiteSpace(name))
            ViewModel.CreateLocalFolder(name);
    }

    private async void OnLocalRenameClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var item = ViewModel.GetFirstSelectedLocalEntry();
        if (item is null) { ViewModel.ShowToast("Check a local file or folder to rename."); return; }
        string? newName = await InputDialog.ShowPromptAsync(this, "Rename Local Item", $"Rename '{item.Name}' to:", item.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != item.Name)
            ViewModel.RenameLocalSelected(newName);
    }

    private async void OnLocalDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        int count = ViewModel.GetSelectedLocalCount();
        if (count == 0) { ViewModel.ShowToast("Check one or more local items to delete."); return; }
        bool ok = await InputDialog.ShowConfirmAsync(this, "Delete Local Items", $"Permanently delete {count} selected local item(s)?");
        if (ok)
            ViewModel.DeleteLocalSelected();
    }

    // --- Remote File Actions ---
    private async void OnRemoteNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanWriteRemote) return;
        string? name = await InputDialog.ShowPromptAsync(this, "New Remote Folder", "Enter new remote folder name:");
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.CreateRemoteFolderAsync(name);
    }

    private async void OnRemoteRenameClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanWriteRemote) return;
        var item = ViewModel.GetFirstSelectedRemoteEntry();
        if (item is null) { ViewModel.ShowToast("Check a remote file or folder to rename."); return; }
        string? newName = await InputDialog.ShowPromptAsync(this, "Rename Remote Item", $"Rename '{item.Name}' to:", item.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != item.Name)
            await ViewModel.RenameRemoteSelectedAsync(newName);
    }

    private async void OnRemoteDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanWriteRemote) return;
        int count = ViewModel.GetSelectedRemoteCount();
        if (count == 0) { ViewModel.ShowToast("Check one or more remote items to delete."); return; }
        bool ok = await InputDialog.ShowConfirmAsync(this, "Delete Remote Items", $"Permanently delete {count} selected remote item(s)?");
        if (ok)
            await ViewModel.DeleteRemoteSelectedAsync();
    }

    // --- Pointer Hooks: Shift+Click Checkboxes, Right-Click Select, Bidirectional Drag & Drop ---
    private static RemoteEntryViewModel? FindEntryFromEventSource(object? source)
    {
        if (source is not Visual visual) return null;
        if (visual is Control { DataContext: RemoteEntryViewModel direct })
        {
            // Ensure it actually belongs to a ListBoxItem (not header/empty space)
            if (visual.FindAncestorOfType<ListBoxItem>() is not null)
                return direct;
        }
        return visual.FindAncestorOfType<ListBoxItem>()?.DataContext as RemoteEntryViewModel;
    }

    private static bool IsInsideInteractiveChrome(object? source, out bool isCheckBox)
    {
        isCheckBox = false;
        if (source is not Visual visual) return false;
        if (visual is CheckBox || visual.FindAncestorOfType<CheckBox>() is not null)
        {
            isCheckBox = true;
            return true;
        }
        return visual is ScrollBar || visual.FindAncestorOfType<ScrollBar>() is not null;
    }

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox listBox) return;

        bool isLocal = listBox.Name == "LocalFileList";
        var entry = FindEntryFromEventSource(e.Source);
        var props = e.GetCurrentPoint(listBox).Properties;

        if (props.IsRightButtonPressed && entry is not null)
        {
            listBox.SelectedItem = entry;
            if (!entry.IsSelected)
            {
                if (isLocal)
                    ViewModel.ClearLocalSelectionCommand.Execute(null);
                else
                    ViewModel.ClearRemoteSelectionCommand.Execute(null);
                entry.IsSelected = true;
            }
            if (isLocal) _lastLocalCheckAnchor = entry;
            else _lastRemoteCheckAnchor = entry;
            return;
        }

        if (!props.IsLeftButtonPressed || entry is null)
            return;

        if (IsInsideInteractiveChrome(e.Source, out bool isCheckBox))
        {
            if (isCheckBox)
            {
                bool shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                var anchor = isLocal ? _lastLocalCheckAnchor : _lastRemoteCheckAnchor;
                if (shiftHeld && anchor is not null && !ReferenceEquals(anchor, entry))
                {
                    bool targetState = !entry.IsSelected;
                    if (isLocal)
                        ViewModel.SelectRangeLocal(anchor, entry, targetState);
                    else
                        ViewModel.SelectRangeRemote(anchor, entry, targetState);
                    e.Handled = true;
                }
                if (isLocal) _lastLocalCheckAnchor = entry;
                else _lastRemoteCheckAnchor = entry;
            }
            return;
        }

        _dragSourceList = listBox;
        _dragEntry = entry;
        _dragStartPoint = e.GetPosition(listBox);
        _isDragging = false;
    }

    private async void OnFileListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragSourceList is null || _dragEntry is null || ViewModel is null)
            return;

        if (sender is not ListBox listBox || !ReferenceEquals(listBox, _dragSourceList))
            return;

        if (!e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            _dragSourceList = null;
            _dragEntry = null;
            return;
        }

        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - _dragStartPoint.X) < 6 && Math.Abs(current.Y - _dragStartPoint.Y) < 6)
            return;

        _isDragging = true;
        bool isLocal = listBox.Name == "LocalFileList";
        var sourceEntry = _dragEntry;
        _dragSourceList = null;
        _dragEntry = null;

        try
        {
            var data = new DataObject();
            if (isLocal)
            {
                var paths = sourceEntry.IsSelected
                    ? ViewModel.GetSelectedLocalPaths().ToList()
                    : [sourceEntry.RelativePath];
                if (paths.Count == 0) return;
                data.Set(LocalDragFormat, paths);
            }
            else
            {
                var entries = sourceEntry.IsSelected
                    ? ViewModel.GetSelectedRemoteEntries().ToList()
                    : [sourceEntry.Entry];
                if (entries.Count == 0) return;
                data.Set(RemoteDragFormat, entries);
            }

            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch
        {
            // Ignore aborted drag operations
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void OnFileListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            _dragSourceList = null;
            _dragEntry = null;
        }
    }

    // --- Bidirectional Drag & Drop (Local <-> Remote & OS Explorer -> Remote) ---
    private void OnLocalDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(RemoteDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnLocalDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.Contains(RemoteDragFormat)) return;
        if (e.Data.Get(RemoteDragFormat) is IReadOnlyList<RemoteEntry> entries && entries.Count > 0)
            await ViewModel.PullEntriesToLocalAsync(entries);
    }

    private void OnRemoteDragOver(object? sender, DragEventArgs e)
    {
        bool canAccept = ViewModel?.CanWriteRemote == true &&
                         (e.Data.Contains(DataFormats.Files) || e.Data.Contains(LocalDragFormat));
        e.DragEffects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnRemoteDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanWriteRemote) return;

        if (e.Data.Contains(LocalDragFormat) && e.Data.Get(LocalDragFormat) is IReadOnlyList<string> localPaths && localPaths.Count > 0)
        {
            await ViewModel.SendLocalPathsToRemoteAsync(localPaths, isDrop: true);
            return;
        }

        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        if (paths.Count > 0)
            await ViewModel.SendDroppedLocalPathsAsync(paths);
    }

    // --- Keyboard Shortcuts (Both Panes) ---
    private void OnLocalListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox listBox) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
        {
            ViewModel.SelectAllLocalCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (listBox.SelectedItem is RemoteEntryViewModel entry)
                {
                    ViewModel.OpenLocalEntry(entry);
                    e.Handled = true;
                }
                break;
            case Key.Back:
                if (ViewModel.CanGoUpLocal)
                {
                    ViewModel.GoUpLocalCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.F5:
                ViewModel.RefreshLocalCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                OnLocalRenameClick(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                OnLocalDeleteClick(sender, e);
                e.Handled = true;
                break;
        }
    }

    private async void OnRemoteListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsConnected || sender is not ListBox listBox) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
        {
            ViewModel.SelectAllRemoteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (listBox.SelectedItem is RemoteEntryViewModel entry)
                {
                    e.Handled = true;
                    await ViewModel.OpenRemoteEntryAsync(entry);
                }
                break;
            case Key.Back:
                if (ViewModel.CanGoUpRemote)
                {
                    e.Handled = true;
                    ViewModel.GoUpRemoteCommand.Execute(null);
                }
                break;
            case Key.F5:
                e.Handled = true;
                ViewModel.RefreshRemoteCommand.Execute(null);
                break;
            case Key.F2:
                if (ViewModel.CanWriteRemote)
                {
                    e.Handled = true;
                    OnRemoteRenameClick(sender, e);
                }
                break;
            case Key.Delete:
                if (ViewModel.CanWriteRemote)
                {
                    e.Handled = true;
                    OnRemoteDeleteClick(sender, e);
                }
                break;
        }
    }

    // --- Context Menu Handlers ---
    private void OnLocalContextSendClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ViewModel.SendSelectedCommand.CanExecute(null))
            ViewModel.SendSelectedCommand.Execute(null);
    }

    private void OnLocalContextOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var list = this.FindControl<ListBox>("LocalFileList");
        var entry = list?.SelectedItem as RemoteEntryViewModel ?? ViewModel.GetFirstSelectedLocalEntry();
        if (entry is null) return;

        if (entry.IsDirectory)
            ViewModel.OpenLocalEntry(entry);
        else
            OpenPathInOsShell(entry.RelativePath, revealInFolder: false);
    }

    private void OnLocalContextRevealClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var list = this.FindControl<ListBox>("LocalFileList");
        var entry = list?.SelectedItem as RemoteEntryViewModel ?? ViewModel.GetFirstSelectedLocalEntry();
        string targetPath = entry?.RelativePath ?? ViewModel.LocalPath;
        if (!string.IsNullOrWhiteSpace(targetPath))
            OpenPathInOsShell(targetPath, revealInFolder: entry is not null);
    }

    private void OnLocalContextRenameClick(object? sender, RoutedEventArgs e) => OnLocalRenameClick(sender, e);
    private void OnLocalContextDeleteClick(object? sender, RoutedEventArgs e) => OnLocalDeleteClick(sender, e);

    private async void OnLocalContextCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null) return;
        var paths = ViewModel.GetSelectedLocalPaths();
        string text = paths.Count > 0 ? string.Join(Environment.NewLine, paths) : ViewModel.LocalPath;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await Clipboard.SetTextAsync(text);
            ViewModel.ShowToast("Local path copied to clipboard");
        }
    }

    private void OnRemoteContextPullClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ViewModel.PullSelectedCommand.CanExecute(null))
            ViewModel.PullSelectedCommand.Execute(null);
    }

    private async void OnRemoteContextOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var list = this.FindControl<ListBox>("RemoteFileList");
        var entry = list?.SelectedItem as RemoteEntryViewModel ?? ViewModel.GetFirstSelectedRemoteEntry();
        if (entry is not null)
            await ViewModel.OpenRemoteEntryAsync(entry);
    }

    private void OnRemoteContextRenameClick(object? sender, RoutedEventArgs e) => OnRemoteRenameClick(sender, e);
    private void OnRemoteContextDeleteClick(object? sender, RoutedEventArgs e) => OnRemoteDeleteClick(sender, e);

    private async void OnRemoteContextCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null) return;
        var entries = ViewModel.GetSelectedRemoteEntries();
        string text = entries.Count > 0
            ? string.Join(Environment.NewLine, entries.Select(x => x.RelativePath))
            : ViewModel.RemotePath;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await Clipboard.SetTextAsync(text);
            ViewModel.ShowToast("Remote path copied to clipboard");
        }
    }

    // --- Open Folder on Completed Pull Transfer ---
    private void OnOpenTransferFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TransferItemViewModel item } && !string.IsNullOrWhiteSpace(item.Model.LocalDestinationPath))
            OpenPathInOsShell(item.Model.LocalDestinationPath, revealInFolder: File.Exists(item.Model.LocalDestinationPath));
    }

    private static void OpenPathInOsShell(string path, bool revealInFolder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (revealInFolder && (File.Exists(path) || Directory.Exists(path)))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    return;
                }
            }

            string target = revealInFolder ? (Path.GetDirectoryName(path) ?? path) : path;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // Ignore OS file manager launch failure
        }
    }

    // --- Quick Text Share Flyout & Received Banner ---
    private void OnOpenShareTextFlyoutClick(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindControl<Button>("ShareTextPillButton");
        var flyout = btn is null ? null : FlyoutBase.GetAttachedFlyout(btn) as Flyout;
        if (btn is not null && flyout is not null)
        {
            flyout.ShowAt(btn);
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("ShareTextBox")?.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnShareTextInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && ViewModel is not null)
        {
            if (ViewModel.SendSharedTextCommand.CanExecute(null))
                ViewModel.SendSharedTextCommand.Execute(null);
            OnSendShareTextCloseFlyoutClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnSendShareTextCloseFlyoutClick(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindControl<Button>("ShareTextPillButton");
        if (btn is not null && FlyoutBase.GetAttachedFlyout(btn) is Flyout flyout)
            flyout.Hide();
    }

    private async void OnCopyReceivedTextClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null || string.IsNullOrEmpty(ViewModel.ReceivedTextContent))
            return;
        await Clipboard.SetTextAsync(ViewModel.ReceivedTextContent);
        ViewModel.ShowToast("Received text copied to clipboard");
    }

    // --- Linux App Menu & Desktop Self-Install ---
    private void OnLinuxSelfInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var (ok, message) = LinuxInstallerHelper.InstallToLinuxDesktop();
        ViewModel.ShowToast(message);
        if (!ok)
            ViewModel.SaveSettingsCommand.Execute(null);
    }

    private void OnRemoveSavedPeerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is Button { DataContext: SavedPeerEntry entry })
            ViewModel.RemoveSavedPeer(entry);
    }

    // --- Existing Header & Picker Handlers ---
    private async void OnCopyPinClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null) return;
        await Clipboard.SetTextAsync(ViewModel.HostPin);
        ViewModel.ShowToast("Pairing PIN copied to clipboard");
        GetHostMenuFlyout()?.Hide();
    }

    private async void OnCopyInstallerUrlClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null || string.IsNullOrWhiteSpace(ViewModel.InstallerUrl)) return;
        await Clipboard.SetTextAsync(ViewModel.InstallerUrl);
    }

    private async void OnCopyLinuxOneLinerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null || string.IsNullOrWhiteSpace(ViewModel.InstallerLinuxOneLiner)) return;
        await Clipboard.SetTextAsync(ViewModel.InstallerLinuxOneLiner);
    }

    private async void OnCopyWindowsOneLinerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null || string.IsNullOrWhiteSpace(ViewModel.InstallerWindowsOneLiner)) return;
        await Clipboard.SetTextAsync(ViewModel.InstallerWindowsOneLiner);
    }

    private async void OnPickInstallerBinariesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string? folder = await PickFolderAsync("Select Folder Containing SwiftBeam Windows/Linux Binaries");
        if (!string.IsNullOrWhiteSpace(folder))
            ViewModel.SetInstallerBinariesDirectory(folder);
    }

    private async void OnPickSharedFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string? folder = await PickFolderAsync("Select Folder to Share");
        if (!string.IsNullOrWhiteSpace(folder))
            ViewModel.SharedDirectory = folder;
    }

    private async void OnPickDownloadFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        string? folder = await PickFolderAsync("Select Default Download Folder");
        if (!string.IsNullOrWhiteSpace(folder))
            ViewModel.DownloadDirectory = folder;
    }

    private void OnLocalEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox listBox) return;
        if (listBox.SelectedItem is RemoteEntryViewModel entry)
            ViewModel.OpenLocalEntry(entry);
    }

    private async void OnRemoteEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox listBox) return;
        if (listBox.SelectedItem is RemoteEntryViewModel entry)
            await ViewModel.OpenRemoteEntryAsync(entry);
    }

    private void OnLocalPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is not null)
            ViewModel.NavigateLocalInputCommand.Execute(null);
    }

    private void OnRemotePathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is not null)
            ViewModel.NavigateRemoteInputCommand.Execute(null);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
