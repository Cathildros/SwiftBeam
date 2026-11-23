using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Settings;
using SwiftBeam.Desktop.ViewModels;

namespace SwiftBeam.Desktop;

public partial class MainWindow : Window
{
    private GridLength _savedTransferPanelHeight = new(210, GridUnitType.Pixel);

    public MainWindow()
    {
        InitializeComponent();

        var dropZone = this.FindControl<Grid>("RemoteDropZone");
        if (dropZone is not null)
        {
            dropZone.AddHandler(DragDrop.DragOverEvent, OnRemoteDragOver);
            dropZone.AddHandler(DragDrop.DropEvent, OnRemoteDrop);
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
        if (ViewModel is null || !ViewModel.IsConnected) return;
        string? name = await InputDialog.ShowPromptAsync(this, "New Remote Folder", "Enter new remote folder name:");
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.CreateRemoteFolderAsync(name);
    }

    private async void OnRemoteRenameClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsConnected) return;
        var item = ViewModel.GetFirstSelectedRemoteEntry();
        if (item is null) { ViewModel.ShowToast("Check a remote file or folder to rename."); return; }
        string? newName = await InputDialog.ShowPromptAsync(this, "Rename Remote Item", $"Rename '{item.Name}' to:", item.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != item.Name)
            await ViewModel.RenameRemoteSelectedAsync(newName);
    }

    private async void OnRemoteDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsConnected) return;
        int count = ViewModel.GetSelectedRemoteCount();
        if (count == 0) { ViewModel.ShowToast("Check one or more remote items to delete."); return; }
        bool ok = await InputDialog.ShowConfirmAsync(this, "Delete Remote Items", $"Permanently delete {count} selected remote item(s)?");
        if (ok)
            await ViewModel.DeleteRemoteSelectedAsync();
    }

    // --- Drag & Drop files from OS Explorer / File Manager onto Remote pane ---
    private void OnRemoteDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnRemoteDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.Contains(DataFormats.Files)) return;
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
