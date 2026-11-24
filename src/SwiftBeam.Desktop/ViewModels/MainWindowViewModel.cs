using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Threading;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Discovery;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;
using SwiftBeam.Core.Server;
using SwiftBeam.Core.Settings;
using SwiftBeam.Core.SystemIntegration;
using SwiftBeam.Core.Updates;

namespace SwiftBeam.Desktop.ViewModels;

public enum ConnectPopoverStep
{
    PickTarget = 0,
    ManualEntry = 1,
    EnterPin = 2
}

public readonly record struct ConnectAttemptResult(bool Success, bool IsAuthError, string? Error);

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly FileShareServer _server;
    private readonly InstallerWebServer _installerServer;
    private readonly PeerDiscoveryService _discovery;
    private readonly TransferClient _browseClient = new();
    private readonly TransferManager _transferManager = new();
    private readonly SleepInhibitor _sleepInhibitor = new();
    private readonly Dictionary<string, TransferItemViewModel> _transferMap = new();

    private readonly List<RemoteEntryViewModel> _allLocalEntries = [];
    private readonly List<RemoteEntryViewModel> _allRemoteEntries = [];

    private static readonly IBrush ServerOnlineBrush = SolidColorBrush.Parse("#10B981");
    private static readonly IBrush ServerOfflineBrush = SolidColorBrush.Parse("#64748B");
    private static readonly IBrush NavActiveBg = SolidColorBrush.Parse("#8B5CF6");
    private static readonly IBrush NavInactiveBg = Brushes.Transparent;
    private static readonly IBrush NavActiveFg = Brushes.White;
    private static readonly IBrush NavInactiveFg = SolidColorBrush.Parse("#94A3B8");

    private int _activeTabIndex; // 0 = File Transfer, 1 = Host & Settings, 2 = Activity Log
    private bool _isTransferPanelExpanded = true;
    private bool _isServerRunning;
    private bool _isInstallerServerRunning;
    private string _installerUrl = string.Empty;
    private string _installerStatusText = string.Empty;
    private int _installerDownloadsCount;
    private int _activeIncomingClients;
    private string _totalServedText = "0 B";
    private string _primaryAddressText = "Detecting network...";

    private PeerItemViewModel? _selectedPeer;
    private SavedPeerEntry? _selectedSavedPeer;
    private PeerEndpoint? _selectedPeerEndpoint;
    private string _connectHost = string.Empty;
    private string _connectPort = ProtocolConstants.DefaultTransferPort.ToString();
    private string _connectPin = string.Empty;
    private bool _rememberPin = true;
    private bool _connectUseTls = true;
    private bool _isConnecting;
    private bool _isConnected;
    private ConnectedPeerMetadata? _connectedPeer;
    private ConnectionTarget? _lastConnectionTarget;
    private DateTime _lastAutoRefreshUtc = DateTime.MinValue;
    private string _connectedEndpointSummary = string.Empty;
    private string _connectionError = string.Empty;
    private ConnectPopoverStep _connectStep = ConnectPopoverStep.PickTarget;
    private string _connectTargetDisplayName = string.Empty;

    // Local browser (Left Pane)
    private string _localPath = string.Empty;
    private string _localPathInput = string.Empty;
    private string _localSearchFilter = string.Empty;
    private string _localSelectionText = "0 items";
    private string _localEmptyMessage = string.Empty;
    private string _localSortColumn = "Name";
    private bool _localSortAsc = true;

    // Remote browser (Right Pane)
    private string _remotePath = string.Empty;
    private string _remotePathInput = string.Empty;
    private string _remoteSearchFilter = string.Empty;
    private string _remoteSelectionText = "Not connected";
    private string _remoteEmptyMessage = string.Empty;
    private string _remoteSortColumn = "Name";
    private bool _remoteSortAsc = true;

    // Telemetry & Completion Toast
    private double _overallProgressPercent;
    private string _overallProgressText = "Ready — select files on either side to Send or Pull in parallel";
    private string _aggregateSpeedText = "0 B/s";
    private string _etaText = "--:--";
    private string _activeWorkersText = "0 / 6 streams";
    private int _prevActiveTransferCount;
    private int _prevCompletedTransferCount;
    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private CancellationTokenSource? _toastCts;
    private bool _hasIncomingPeer;
    private string _incomingPeerBadge = "PEER CONNECTED";
    private string _incomingPeerSummary = string.Empty;
    private string _trayTooltipText = "SwiftBeam";

    private readonly GitHubUpdateService _updateService = new();
    private UpdateReleaseInfo? _pendingUpdate;
    private bool _hasAvailableUpdate;
    private string _availableUpdateText = string.Empty;
    private string _updateStatusText = string.Empty;
    private bool _isCheckingForUpdates;
    private bool _isDownloadingUpdate;
    private double _updateDownloadProgress;

    private string _shareTextInput = string.Empty;
    private string _receivedTextSender = string.Empty;
    private string _receivedTextContent = string.Empty;
    private bool _hasReceivedText;

    public ObservableCollection<LocalInterfaceAddress> LocalInterfaces { get; } = [];
    public ObservableCollection<string> InstallerUrls { get; } = [];
    public ObservableCollection<PeerItemViewModel> DiscoveredPeers { get; } = [];
    public ObservableCollection<SavedPeerEntry> SavedPeers { get; } = [];
    public ObservableCollection<PeerEndpoint> SelectedPeerEndpoints { get; } = [];
    public ObservableCollection<RemoteEntryViewModel> FilteredLocalEntries { get; } = [];
    public ObservableCollection<RemoteEntryViewModel> FilteredRemoteEntries { get; } = [];
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    // --- Header Navigation & Collapsible Transfer Drawer ---
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (SetProperty(ref _activeTabIndex, value))
            {
                OnPropertyChanged(nameof(IsTransferTabActive));
                OnPropertyChanged(nameof(IsSettingsTabActive));
                OnPropertyChanged(nameof(IsLogTabActive));
                OnPropertyChanged(nameof(TransferTabBg));
                OnPropertyChanged(nameof(SettingsTabBg));
                OnPropertyChanged(nameof(LogTabBg));
                OnPropertyChanged(nameof(TransferTabFg));
                OnPropertyChanged(nameof(SettingsTabFg));
                OnPropertyChanged(nameof(LogTabFg));
            }
        }
    }

    public bool IsTransferTabActive => _activeTabIndex == 0;
    public bool IsSettingsTabActive => _activeTabIndex == 1;
    public bool IsLogTabActive => _activeTabIndex == 2;
    public IBrush TransferTabBg => IsTransferTabActive ? NavActiveBg : NavInactiveBg;
    public IBrush SettingsTabBg => IsSettingsTabActive ? NavActiveBg : NavInactiveBg;
    public IBrush LogTabBg => IsLogTabActive ? NavActiveBg : NavInactiveBg;
    public IBrush TransferTabFg => IsTransferTabActive ? NavActiveFg : NavInactiveFg;
    public IBrush SettingsTabFg => IsSettingsTabActive ? NavActiveFg : NavInactiveFg;
    public IBrush LogTabFg => IsLogTabActive ? NavActiveFg : NavInactiveFg;

    public bool IsTransferPanelExpanded
    {
        get => _isTransferPanelExpanded;
        set
        {
            if (SetProperty(ref _isTransferPanelExpanded, value))
            {
                OnPropertyChanged(nameof(IsTransferPanelCollapsed));
                OnPropertyChanged(nameof(TransferCollapseButtonText));
            }
        }
    }

    public bool IsTransferPanelCollapsed => !IsTransferPanelExpanded;
    public string TransferCollapseButtonText => IsTransferPanelExpanded ? "▾ Hide Queue" : "▴ Show Queue";

    // --- Host / Settings properties ---
    public string DeviceName
    {
        get => _settings.DeviceName;
        set
        {
            if (_settings.DeviceName != value)
            {
                _settings.DeviceName = value;
                _server.Options.DeviceName = value;
                OnPropertyChanged();
            }
        }
    }

    public int ListenPort
    {
        get => _settings.ListenPort;
        set
        {
            int clamped = Math.Clamp(value, 1024, 65535);
            if (_settings.ListenPort != clamped)
            {
                _settings.ListenPort = clamped;
                OnPropertyChanged();
            }
        }
    }

    public bool AllowFullFilesystemAccess
    {
        get => _settings.AllowFullFilesystemAccess;
        set
        {
            if (_settings.AllowFullFilesystemAccess != value)
            {
                _settings.AllowFullFilesystemAccess = value;
                _server.UpdateFullFilesystemAccess(value);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public string SharedDirectory
    {
        get => _settings.SharedDirectory;
        set
        {
            if (_settings.SharedDirectory != value)
            {
                _settings.SharedDirectory = value;
                _server.UpdateSharedDirectory(value);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public string DownloadDirectory
    {
        get => _settings.DownloadDirectory;
        set
        {
            if (_settings.DownloadDirectory != value)
            {
                _settings.DownloadDirectory = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public string HostPin
    {
        get => _settings.Pin;
        set
        {
            string norm = PinAuth.NormalizePin(value);
            if (_settings.Pin != norm)
            {
                _settings.Pin = norm;
                _server.UpdatePin(norm, _settings.RequirePin);
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HostPinFormatted));
            }
        }
    }

    public string HostPinFormatted => _settings.RequirePin ? PinAuth.FormatPin(_settings.Pin) : "PIN Off";

    public bool HostRequirePin
    {
        get => _settings.RequirePin;
        set
        {
            if (_settings.RequirePin != value)
            {
                _settings.RequirePin = value;
                _server.UpdatePin(_settings.Pin, value);
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HostPinFormatted));
            }
        }
    }

    public bool HostAllowUnencrypted
    {
        get => _settings.AllowUnencryptedConnections;
        set
        {
            if (_settings.AllowUnencryptedConnections != value)
            {
                _settings.AllowUnencryptedConnections = value;
                _server.UpdateAllowUnencrypted(value);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool HostReadOnlyShare
    {
        get => _settings.ReadOnlyShare;
        set
        {
            if (_settings.ReadOnlyShare != value)
            {
                _settings.ReadOnlyShare = value;
                _server.UpdateReadOnly(value);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool HostRequireApproval
    {
        get => _settings.RequireHostApproval;
        set
        {
            if (_settings.RequireHostApproval != value)
            {
                _settings.RequireHostApproval = value;
                _server.UpdateRequireHostApproval(value);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool UseSmartCompression
    {
        get => _settings.UseSmartCompression;
        set
        {
            if (_settings.UseSmartCompression != value)
            {
                _settings.UseSmartCompression = value;
                _transferManager.UseSmartCompression = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool PreventSleepDuringTransfers
    {
        get => _settings.PreventSleepDuringTransfers;
        set
        {
            if (_settings.PreventSleepDuringTransfers != value)
            {
                _settings.PreventSleepDuringTransfers = value;
                if (!value)
                    _sleepInhibitor.SetActive(false);
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool StartWithSystem
    {
        get => _settings.StartWithSystem;
        set
        {
            if (_settings.StartWithSystem != value)
            {
                _settings.StartWithSystem = value;
                bool ok = AutoStartHelper.TrySetAutoStart(value);
                if (!ok)
                    AppendLog("Could not update OS startup entry on this platform.");
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool IsLinuxPlatform => OperatingSystem.IsLinux();

    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set
        {
            if (SetProperty(ref _isServerRunning, value))
            {
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerToggleText));
                OnPropertyChanged(nameof(ServerStatusBrush));
            }
        }
    }

    public string ServerStatusText => IsServerRunning
        ? $"Sharing ON • Port {_server.ListeningPort} • {ActiveIncomingClients} active"
        : "Sharing OFF";

    public string ServerToggleText => IsServerRunning ? "Stop Sharing" : "Start Sharing";
    public IBrush ServerStatusBrush => IsServerRunning ? ServerOnlineBrush : ServerOfflineBrush;

    public int ActiveIncomingClients
    {
        get => _activeIncomingClients;
        private set
        {
            if (SetProperty(ref _activeIncomingClients, value))
                OnPropertyChanged(nameof(ServerStatusText));
        }
    }

    public string TotalServedText
    {
        get => _totalServedText;
        private set => SetProperty(ref _totalServedText, value);
    }

    public string PrimaryAddressText
    {
        get => _primaryAddressText;
        private set => SetProperty(ref _primaryAddressText, value);
    }

    public string CertFingerprint => _server.CertFingerprint;

    // --- 1-Click Local Installer Download Page properties ---
    public bool IsInstallerServerRunning
    {
        get => _isInstallerServerRunning;
        private set
        {
            if (SetProperty(ref _isInstallerServerRunning, value))
            {
                OnPropertyChanged(nameof(InstallerToggleText));
                OnPropertyChanged(nameof(InstallerToggleShortText));
                OnPropertyChanged(nameof(InstallerLinuxOneLiner));
                OnPropertyChanged(nameof(InstallerWindowsOneLiner));
            }
        }
    }

    public string InstallerToggleText => IsInstallerServerRunning ? "Stop App Link" : "Share App Link";
    public string InstallerToggleShortText => IsInstallerServerRunning ? "Stop Link" : "Share App";

    public string InstallerUrl
    {
        get => _installerUrl;
        private set
        {
            if (SetProperty(ref _installerUrl, value))
            {
                OnPropertyChanged(nameof(InstallerLinuxOneLiner));
                OnPropertyChanged(nameof(InstallerWindowsOneLiner));
            }
        }
    }

    public string InstallerStatusText
    {
        get => _installerStatusText;
        private set => SetProperty(ref _installerStatusText, value);
    }

    public string InstallerLinuxOneLiner => string.IsNullOrEmpty(InstallerUrl)
        ? string.Empty
        : $"curl -fsSL {InstallerUrl}/install.sh | bash";

    public string InstallerWindowsOneLiner => string.IsNullOrEmpty(InstallerUrl)
        ? string.Empty
        : $"irm {InstallerUrl}/install.ps1 | iex";

    // --- Connection Bar / Top-Bar Shifting Popover properties ---
    public ConnectPopoverStep ConnectStep
    {
        get => _connectStep;
        set
        {
            if (SetProperty(ref _connectStep, value))
            {
                OnPropertyChanged(nameof(IsPickTargetStep));
                OnPropertyChanged(nameof(IsManualEntryStep));
                OnPropertyChanged(nameof(IsEnterPinStep));
            }
        }
    }

    public bool IsPickTargetStep => _connectStep == ConnectPopoverStep.PickTarget;
    public bool IsManualEntryStep => _connectStep == ConnectPopoverStep.ManualEntry;
    public bool IsEnterPinStep => _connectStep == ConnectPopoverStep.EnterPin;
    public bool HasNoDiscoveredPeers => DiscoveredPeers.Count == 0;

    public string ConnectTargetDisplayName
    {
        get => _connectTargetDisplayName;
        set
        {
            if (SetProperty(ref _connectTargetDisplayName, value))
            {
                OnPropertyChanged(nameof(ConnectSelectorLabel));
                OnPropertyChanged(nameof(ConnectTargetSubtitle));
            }
        }
    }

    public string ConnectSelectorLabel => !string.IsNullOrWhiteSpace(_connectTargetDisplayName)
        ? $"{_connectTargetDisplayName} ▾"
        : (!string.IsNullOrWhiteSpace(ConnectHost) ? $"{ConnectHost.Trim()} ▾" : "Select PC ▾");

    public string ConnectTargetSubtitle
    {
        get
        {
            string host = ConnectHost.Trim();
            if (string.IsNullOrEmpty(host)) return string.Empty;
            string port = string.IsNullOrWhiteSpace(ConnectPort) ? ProtocolConstants.DefaultTransferPort.ToString() : ConnectPort.Trim();
            return SelectedPeerEndpoint is not null
                ? $"{host}:{port} • {SelectedPeerEndpoint.LinkBadge}"
                : $"{host}:{port}";
        }
    }

    public PeerItemViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value) && value is not null)
            {
                SelectedPeerEndpoints.Clear();
                foreach (var ep in value.Endpoints)
                    SelectedPeerEndpoints.Add(ep);

                SelectedPeerEndpoint = value.Model.BestEndpoint;
                if (SelectedPeerEndpoint is not null)
                {
                    ConnectHost = SelectedPeerEndpoint.Host;
                    ConnectPort = SelectedPeerEndpoint.Port.ToString();
                }

                ConnectionError = string.Empty;
                OnPropertyChanged(nameof(HasMultipleEndpoints));
                OnPropertyChanged(nameof(ConnectSelectorLabel));
                OnPropertyChanged(nameof(ConnectTargetSubtitle));
            }
        }
    }

    public bool HasMultipleEndpoints => SelectedPeerEndpoints.Count > 1;

    public PeerEndpoint? SelectedPeerEndpoint
    {
        get => _selectedPeerEndpoint;
        set
        {
            if (SetProperty(ref _selectedPeerEndpoint, value) && value is not null)
            {
                ConnectHost = value.Host;
                ConnectPort = value.Port.ToString();
                OnPropertyChanged(nameof(ConnectTargetSubtitle));
            }
        }
    }

    public string ConnectHost
    {
        get => _connectHost;
        set
        {
            if (SetProperty(ref _connectHost, value))
            {
                OnPropertyChanged(nameof(ConnectSelectorLabel));
                OnPropertyChanged(nameof(ConnectTargetSubtitle));
            }
        }
    }

    public string ConnectPort
    {
        get => _connectPort;
        set
        {
            if (SetProperty(ref _connectPort, value))
                OnPropertyChanged(nameof(ConnectTargetSubtitle));
        }
    }

    public string ConnectPin
    {
        get => _connectPin;
        set => SetProperty(ref _connectPin, value);
    }

    public bool RememberPin
    {
        get => _rememberPin;
        set => SetProperty(ref _rememberPin, value);
    }

    public bool HasSavedPeers => SavedPeers.Count > 0;

    public SavedPeerEntry? SelectedSavedPeer
    {
        get => _selectedSavedPeer;
        set
        {
            if (SetProperty(ref _selectedSavedPeer, value) && value is not null)
            {
                ConnectHost = value.Host;
                ConnectPort = value.Port.ToString();
                ConnectUseTls = value.UseTls;
                if (!string.IsNullOrEmpty(value.SavedPin))
                    ConnectPin = value.SavedPin;
                ConnectionError = string.Empty;
            }
        }
    }

    public string LocalNameHeader => HeaderWithArrow("Name", _localSortColumn, _localSortAsc);
    public string LocalTypeHeader => HeaderWithArrow("Type", _localSortColumn, _localSortAsc);
    public string LocalModifiedHeader => HeaderWithArrow("Modified", _localSortColumn, _localSortAsc);
    public string LocalSizeHeader => HeaderWithArrow("Size", _localSortColumn, _localSortAsc);

    public string RemoteNameHeader => HeaderWithArrow("Name", _remoteSortColumn, _remoteSortAsc);
    public string RemoteTypeHeader => HeaderWithArrow("Type", _remoteSortColumn, _remoteSortAsc);
    public string RemoteModifiedHeader => HeaderWithArrow("Modified", _remoteSortColumn, _remoteSortAsc);
    public string RemoteSizeHeader => HeaderWithArrow("Size", _remoteSortColumn, _remoteSortAsc);

    private static string HeaderWithArrow(string col, string activeCol, bool asc)
        => col.Equals(activeCol, StringComparison.OrdinalIgnoreCase) ? $"{col} {(asc ? "▲" : "▼")}" : col;

    public int ConflictPolicyIndex
    {
        get => (int)_settings.ConflictPolicy;
        set
        {
            var policy = (FileConflictPolicy)Math.Clamp(value, 0, 2);
            if (_settings.ConflictPolicy != policy)
            {
                _settings.ConflictPolicy = policy;
                _transferManager.ConflictPolicy = policy;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public int MaxSpeedMBps
    {
        get => _settings.MaxSpeedMBps;
        set
        {
            int clamped = Math.Clamp(value, 0, 10_000);
            if (_settings.MaxSpeedMBps != clamped)
            {
                _settings.MaxSpeedMBps = clamped;
                _transferManager.MaxSpeedMBps = clamped;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpeedLimitLabel));
            }
        }
    }

    public string SpeedLimitLabel => MaxSpeedMBps <= 0 ? "Unlimited" : $"{MaxSpeedMBps} MB/s";

    public bool MinimizeToTrayOnClose
    {
        get => _settings.MinimizeToTrayOnClose;
        set
        {
            if (_settings.MinimizeToTrayOnClose != value)
            {
                _settings.MinimizeToTrayOnClose = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool NotifyOnTransferComplete
    {
        get => _settings.NotifyOnTransferComplete;
        set
        {
            if (_settings.NotifyOnTransferComplete != value)
            {
                _settings.NotifyOnTransferComplete = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    // --- GitHub Auto-Update & OS Hooks ---
    public Action? RequestAppShutdown { get; set; }
    public Func<string, string, bool, Task<bool>>? HostApprovalPromptHandler { get; set; }
    public Func<string, Task>? RequestClipboardCopy { get; set; }
    public string AppVersionText { get; } = $"v{GitHubUpdateService.GetCurrentVersion()}";

    public string UpdateRepo
    {
        get => _settings.UpdateRepo;
        set
        {
            string norm = GitHubUpdateService.NormalizeRepoSlug(value);
            if (string.IsNullOrWhiteSpace(norm))
                norm = GitHubUpdateService.DefaultRepo;
            if (_settings.UpdateRepo != norm)
            {
                _settings.UpdateRepo = norm;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool AutoCheckUpdates
    {
        get => _settings.AutoCheckUpdates;
        set
        {
            if (_settings.AutoCheckUpdates != value)
            {
                _settings.AutoCheckUpdates = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool HasAvailableUpdate
    {
        get => _hasAvailableUpdate;
        private set => SetProperty(ref _hasAvailableUpdate, value);
    }

    public string AvailableUpdateText
    {
        get => _availableUpdateText;
        private set => SetProperty(ref _availableUpdateText, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                OnPropertyChanged(nameof(UpdateCheckButtonText));
                CheckForUpdatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string UpdateCheckButtonText => IsCheckingForUpdates ? "Checking..." : "Check for Updates";

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (SetProperty(ref _isDownloadingUpdate, value))
            {
                OnPropertyChanged(nameof(UpdateActionButtonText));
                CheckForUpdatesCommand.RaiseCanExecuteChanged();
                InstallUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string UpdateActionButtonText => IsDownloadingUpdate
        ? $"Downloading {UpdateDownloadProgress:0}%..."
        : "Update & Restart";

    public double UpdateDownloadProgress
    {
        get => _updateDownloadProgress;
        private set
        {
            if (SetProperty(ref _updateDownloadProgress, value))
                OnPropertyChanged(nameof(UpdateActionButtonText));
        }
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool HasIncomingPeer
    {
        get => _hasIncomingPeer;
        private set => SetProperty(ref _hasIncomingPeer, value);
    }

    public string IncomingPeerBadge
    {
        get => _incomingPeerBadge;
        private set => SetProperty(ref _incomingPeerBadge, value);
    }

    public string IncomingPeerSummary
    {
        get => _incomingPeerSummary;
        private set => SetProperty(ref _incomingPeerSummary, value);
    }

    public string TrayTooltipText
    {
        get => _trayTooltipText;
        private set => SetProperty(ref _trayTooltipText, value);
    }

    // --- Quick Text / Clipboard Share ---
    public string ShareTextInput
    {
        get => _shareTextInput;
        set
        {
            if (SetProperty(ref _shareTextInput, value))
                SendSharedTextCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasReceivedText
    {
        get => _hasReceivedText;
        private set => SetProperty(ref _hasReceivedText, value);
    }

    public string ReceivedTextSender
    {
        get => _receivedTextSender;
        private set => SetProperty(ref _receivedTextSender, value);
    }

    public string ReceivedTextContent
    {
        get => _receivedTextContent;
        private set
        {
            if (SetProperty(ref _receivedTextContent, value))
                OnPropertyChanged(nameof(ReceivedTextPreview));
        }
    }

    public string ReceivedTextPreview
    {
        get
        {
            string flat = _receivedTextContent.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length > 110 ? flat[..107] + "..." : flat;
        }
    }

    public bool ConnectUseTls
    {
        get => _connectUseTls;
        set
        {
            if (SetProperty(ref _connectUseTls, value))
            {
                _settings.PreferTls = value;
                _settings.Save();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
                OnPropertyChanged(nameof(ConnectButtonText));
        }
    }

    public string ConnectButtonText => IsConnecting ? "Connecting..." : "Connect";

    public string ConnectedPeerShortName
    {
        get
        {
            if (!IsConnected || _connectedPeer is null) return "Not connected";
            string shortLink = SelectedPeerEndpoint?.LinkBadge ?? (_connectedPeer.TlsActive ? "TLS" : "Direct");
            return $"{_connectedPeer.DeviceName} • {shortLink}";
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(IsRemoteReadOnly));
                OnPropertyChanged(nameof(CanWriteRemote));
                OnPropertyChanged(nameof(RemotePaneTitle));
                OnPropertyChanged(nameof(ConnectedPeerSummary));
                OnPropertyChanged(nameof(ConnectedPeerShortName));
                OnPropertyChanged(nameof(RemoteHasFullFs));
                OnPropertyChanged(nameof(CanGoUpRemote));
                PullSelectedCommand.RaiseCanExecuteChanged();
                SendSelectedCommand.RaiseCanExecuteChanged();
                SendSharedTextCommand.RaiseCanExecuteChanged();
                RefreshRemoteCommand.RaiseCanExecuteChanged();
                GoUpRemoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDisconnected => !IsConnected;
    public bool IsRemoteReadOnly => IsConnected && (_connectedPeer?.ReadOnly ?? false);
    public bool CanWriteRemote => IsConnected && !IsRemoteReadOnly;
    public bool RemoteHasFullFs => IsConnected && (_connectedPeer?.FullFilesystemAccess ?? false);

    public string RemotePaneTitle => IsConnected && _connectedPeer is not null
        ? $"Remote: {_connectedPeer.DeviceName} ({_connectedPeer.OsPlatform}){(IsRemoteReadOnly ? " (Read-Only)" : string.Empty)}"
        : "Remote computer";

    public string ConnectedPeerSummary => IsConnected && _connectedPeer is not null
        ? $"{_connectedPeer.DeviceName} ({_connectedPeer.OsPlatform}) • {_connectedEndpointSummary}"
        : "Not connected";

    public string ConnectionError
    {
        get => _connectionError;
        private set
        {
            if (SetProperty(ref _connectionError, value))
                OnPropertyChanged(nameof(HasConnectionError));
        }
    }

    public bool HasConnectionError => !string.IsNullOrWhiteSpace(ConnectionError);

    // --- Local File Pane (Left) ---
    public string LocalPath
    {
        get => _localPath;
        private set
        {
            if (SetProperty(ref _localPath, value))
            {
                LocalPathInput = value == FileShareServer.DrivesVirtualPath ? "This PC / Drives" : value;
                OnPropertyChanged(nameof(CanGoUpLocal));
                GoUpLocalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LocalPathInput
    {
        get => _localPathInput;
        set => SetProperty(ref _localPathInput, value);
    }

    public bool CanGoUpLocal => !string.IsNullOrEmpty(LocalPath) && LocalPath != FileShareServer.DrivesVirtualPath;

    public string LocalSearchFilter
    {
        get => _localSearchFilter;
        set
        {
            if (SetProperty(ref _localSearchFilter, value))
                ApplyLocalFilter();
        }
    }

    public string LocalSelectionText
    {
        get => _localSelectionText;
        private set => SetProperty(ref _localSelectionText, value);
    }

    public string LocalEmptyMessage
    {
        get => _localEmptyMessage;
        private set
        {
            if (SetProperty(ref _localEmptyMessage, value))
                OnPropertyChanged(nameof(HasLocalEmptyMessage));
        }
    }

    public bool HasLocalEmptyMessage => !string.IsNullOrEmpty(LocalEmptyMessage);

    // --- Remote File Pane (Right) ---
    public string RemotePath
    {
        get => _remotePath;
        private set
        {
            if (SetProperty(ref _remotePath, value))
            {
                RemotePathInput = value == FileShareServer.DrivesVirtualPath
                    ? "Drives / Roots"
                    : (string.IsNullOrEmpty(value) ? "/" : value);
                OnPropertyChanged(nameof(CanGoUpRemote));
                GoUpRemoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RemotePathInput
    {
        get => _remotePathInput;
        set => SetProperty(ref _remotePathInput, value);
    }

    public bool CanGoUpRemote => IsConnected && !string.IsNullOrEmpty(RemotePath) && RemotePath != FileShareServer.DrivesVirtualPath;

    public string RemoteSearchFilter
    {
        get => _remoteSearchFilter;
        set
        {
            if (SetProperty(ref _remoteSearchFilter, value))
                ApplyRemoteFilter();
        }
    }

    public string RemoteSelectionText
    {
        get => _remoteSelectionText;
        private set => SetProperty(ref _remoteSelectionText, value);
    }

    public string RemoteEmptyMessage
    {
        get => _remoteEmptyMessage;
        private set
        {
            if (SetProperty(ref _remoteEmptyMessage, value))
                OnPropertyChanged(nameof(HasRemoteEmptyMessage));
        }
    }

    public bool HasRemoteEmptyMessage => IsConnected && !string.IsNullOrEmpty(RemoteEmptyMessage);

    // --- Transfer Queue & Telemetry ---
    public int ParallelWorkers
    {
        get => _settings.ParallelWorkers;
        set
        {
            int clamped = Math.Clamp(value, 1, 16);
            if (_settings.ParallelWorkers != clamped)
            {
                _settings.ParallelWorkers = clamped;
                _transferManager.MaxConcurrency = clamped;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParallelWorkersLabel));
            }
        }
    }

    public string ParallelWorkersLabel => $"{ParallelWorkers} streams";

    public int ChunkSizeMb
    {
        get => _settings.ChunkSizeMb;
        set
        {
            int clamped = Math.Clamp(value, 1, 64);
            if (_settings.ChunkSizeMb != clamped)
            {
                _settings.ChunkSizeMb = clamped;
                _transferManager.ChunkSizeBytes = clamped * 1024 * 1024;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool VerifySha256
    {
        get => _settings.VerifySha256OnComplete;
        set
        {
            if (_settings.VerifySha256OnComplete != value)
            {
                _settings.VerifySha256OnComplete = value;
                _transferManager.VerifySha256 = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public double OverallProgressPercent
    {
        get => _overallProgressPercent;
        private set => SetProperty(ref _overallProgressPercent, value);
    }

    public string OverallProgressText
    {
        get => _overallProgressText;
        private set => SetProperty(ref _overallProgressText, value);
    }

    public string AggregateSpeedText
    {
        get => _aggregateSpeedText;
        private set => SetProperty(ref _aggregateSpeedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public string ActiveWorkersText
    {
        get => _activeWorkersText;
        private set => SetProperty(ref _activeWorkersText, value);
    }

    // --- Commands ---
    public RelayCommand ShowTransferTabCommand { get; }
    public RelayCommand ShowSettingsTabCommand { get; }
    public RelayCommand ShowLogTabCommand { get; }
    public AsyncRelayCommand ToggleServerCommand { get; }
    public AsyncRelayCommand DisconnectIncomingPeersCommand { get; }
    public AsyncRelayCommand ToggleInstallerServerCommand { get; }
    public RelayCommand RegeneratePinCommand { get; }
    public RelayCommand RefreshInterfacesCommand { get; }
    public AsyncRelayCommand ScanPeersCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand ClearConnectionErrorCommand { get; }

    // Local pane commands
    public RelayCommand LocalDrivesCommand { get; }
    public RelayCommand LocalHomeCommand { get; }
    public RelayCommand LocalDesktopCommand { get; }
    public RelayCommand LocalDownloadsCommand { get; }
    public RelayCommand LocalDocumentsCommand { get; }
    public RelayCommand GoUpLocalCommand { get; }
    public RelayCommand RefreshLocalCommand { get; }
    public RelayCommand NavigateLocalInputCommand { get; }
    public RelayCommand SelectAllLocalCommand { get; }
    public RelayCommand ClearLocalSelectionCommand { get; }
    public AsyncRelayCommand SendSelectedCommand { get; }

    // Remote pane commands
    public AsyncRelayCommand RemoteDrivesCommand { get; }
    public AsyncRelayCommand RemoteHomeCommand { get; }
    public AsyncRelayCommand RemoteDesktopCommand { get; }
    public AsyncRelayCommand RemoteDownloadsCommand { get; }
    public AsyncRelayCommand RemoteDocumentsCommand { get; }
    public AsyncRelayCommand GoUpRemoteCommand { get; }
    public AsyncRelayCommand RefreshRemoteCommand { get; }
    public AsyncRelayCommand NavigateRemoteInputCommand { get; }
    public RelayCommand SelectAllRemoteCommand { get; }
    public RelayCommand ClearRemoteSelectionCommand { get; }
    public AsyncRelayCommand PullSelectedCommand { get; }

    // Queue commands
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand ClearAllTransfersCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateBannerCommand { get; }
    public AsyncRelayCommand SendSharedTextCommand { get; }
    public RelayCommand DismissReceivedTextCommand { get; }

    public MainWindowViewModel()
    {
        _settings = AppSettings.Load();
        _connectUseTls = _settings.PreferTls;

        _settings.SharedDirectory = AppSettings.EnsureWritableDirectory(_settings.SharedDirectory, "Share");
        _settings.DownloadDirectory = AppSettings.EnsureWritableDirectory(_settings.DownloadDirectory, "Downloads");

        var serverOpts = new FileShareServerOptions
        {
            DeviceName = _settings.DeviceName,
            Port = _settings.ListenPort,
            SharedDirectory = _settings.SharedDirectory,
            AllowFullFilesystem = _settings.AllowFullFilesystemAccess,
            RequirePin = _settings.RequirePin,
            Pin = _settings.Pin,
            AllowUnencrypted = _settings.AllowUnencryptedConnections,
            ReadOnly = _settings.ReadOnlyShare,
            RequireHostApproval = _settings.RequireHostApproval
        };

        _server = new FileShareServer(serverOpts);
        _server.Log += AppendLog;
        _server.ConnectionApprovalHandler = (name, ip, isWrite) =>
            HostApprovalPromptHandler is not null
                ? HostApprovalPromptHandler(name, ip, isWrite)
                : Task.FromResult(true);
        _server.TextReceived += OnServerTextReceived;
        _server.StatsChanged += (active, bytes) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ActiveIncomingClients = active;
                TotalServedText = TransferManager.FormatBytes(bytes);
            });
        };
        _server.IncomingActivityChanged += OnIncomingActivityChanged;
        _trayTooltipText = $"SwiftBeam ({_settings.DeviceName})";

        _installerServer = new InstallerWebServer(
            () => _settings.DeviceName,
            () => _server.IsRunning ? _server.ListeningPort : _settings.ListenPort);
        _installerServer.Log += AppendLog;
        _installerServer.DownloadCountChanged += count =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _installerDownloadsCount = count;
                UpdateInstallerStatusText();
            });
        };
        UpdateInstallerStatusText();

        _discovery = new PeerDiscoveryService(
            serverOpts.ServerId,
            _settings.DiscoveryPort,
            () => (_settings.DeviceName, _server.ListeningPort, _settings.RequirePin, _settings.AllowUnencryptedConnections, _server.IsRunning));
        _discovery.Log += AppendLog;
        _discovery.PeersChanged += OnPeersChanged;

        _transferManager.MaxConcurrency = _settings.ParallelWorkers;
        _transferManager.ChunkSizeBytes = _settings.ChunkSizeMb * 1024 * 1024;
        _transferManager.VerifySha256 = _settings.VerifySha256OnComplete;
        _transferManager.ConflictPolicy = _settings.ConflictPolicy;
        _transferManager.MaxSpeedMBps = _settings.MaxSpeedMBps;
        _transferManager.UseSmartCompression = _settings.UseSmartCompression;
        _transferManager.Log += AppendLog;
        _transferManager.ItemAdded += OnTransferItemAdded;
        _transferManager.ItemUpdated += OnTransferItemUpdated;
        _transferManager.ItemRemoved += OnTransferItemRemoved;
        _transferManager.TelemetryUpdated += OnTelemetryUpdated;

        ReloadSavedPeersCollection();

        ShowTransferTabCommand = new RelayCommand(() => ActiveTabIndex = 0);
        ShowSettingsTabCommand = new RelayCommand(() => ActiveTabIndex = 1);
        ShowLogTabCommand = new RelayCommand(() => ActiveTabIndex = 2);
        ToggleServerCommand = new AsyncRelayCommand(ToggleServerAsync);
        DisconnectIncomingPeersCommand = new AsyncRelayCommand(() => _server.DisconnectIncomingClientsAsync());
        ToggleInstallerServerCommand = new AsyncRelayCommand(ToggleInstallerServerAsync);
        RegeneratePinCommand = new RelayCommand(() =>
        {
            HostPin = PinAuth.GeneratePin();
            AppendLog($"Generated new pairing PIN: {HostPinFormatted}");
        });
        RefreshInterfacesCommand = new RelayCommand(RefreshLocalInterfaces);
        ScanPeersCommand = new AsyncRelayCommand(() => _discovery.SendProbeNowAsync());
        ConnectCommand = new AsyncRelayCommand(async () => await TryConnectAsync());
        DisconnectCommand = new AsyncRelayCommand(DisconnectPeerAsync);
        ClearConnectionErrorCommand = new RelayCommand(() => ConnectionError = string.Empty);

        // Local pane commands
        LocalDrivesCommand = new RelayCommand(() => LoadLocalDirectory(FileShareServer.DrivesVirtualPath));
        LocalHomeCommand = new RelayCommand(() => LoadLocalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        LocalDesktopCommand = new RelayCommand(() => LoadLocalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        LocalDownloadsCommand = new RelayCommand(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dl = Path.Combine(home, "Downloads");
            LoadLocalDirectory(Directory.Exists(dl) ? dl : home);
        });
        LocalDocumentsCommand = new RelayCommand(() => LoadLocalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        GoUpLocalCommand = new RelayCommand(GoUpLocalDirectory, () => CanGoUpLocal);
        RefreshLocalCommand = new RelayCommand(() => LoadLocalDirectory(LocalPath));
        NavigateLocalInputCommand = new RelayCommand(() => LoadLocalDirectory(LocalPathInput));
        SelectAllLocalCommand = new RelayCommand(() => SetLocalSelectionAll(true));
        ClearLocalSelectionCommand = new RelayCommand(() => SetLocalSelectionAll(false));
        SendSelectedCommand = new AsyncRelayCommand(SendSelectedToRemoteAsync, () => CanWriteRemote);

        // Remote pane commands
        RemoteDrivesCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(FileShareServer.DrivesVirtualPath));
        RemoteHomeCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(_connectedPeer?.HomePath ?? FileShareServer.DrivesVirtualPath));
        RemoteDesktopCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(_connectedPeer?.DesktopPath ?? _connectedPeer?.HomePath ?? FileShareServer.DrivesVirtualPath));
        RemoteDownloadsCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(_connectedPeer?.DownloadsPath ?? _connectedPeer?.HomePath ?? FileShareServer.DrivesVirtualPath));
        RemoteDocumentsCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(_connectedPeer?.DocumentsPath ?? _connectedPeer?.HomePath ?? FileShareServer.DrivesVirtualPath));
        GoUpRemoteCommand = new AsyncRelayCommand(GoUpRemoteDirectoryAsync, () => CanGoUpRemote);
        RefreshRemoteCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(RemotePath), () => IsConnected);
        NavigateRemoteInputCommand = new AsyncRelayCommand(() => LoadRemoteDirectoryAsync(RemotePathInput));
        SelectAllRemoteCommand = new RelayCommand(() => SetRemoteSelectionAll(true));
        ClearRemoteSelectionCommand = new RelayCommand(() => SetRemoteSelectionAll(false));
        PullSelectedCommand = new AsyncRelayCommand(PullSelectedToLocalAsync, () => IsConnected);

        PauseAllCommand = new RelayCommand(() => { _transferManager.PauseAll(); RefreshAllTransferItemsUi(); });
        ResumeAllCommand = new RelayCommand(() => { _transferManager.ResumeAll(); RefreshAllTransferItemsUi(); });
        CancelAllCommand = new RelayCommand(() => _transferManager.CancelAll());
        ClearFinishedCommand = new RelayCommand(() =>
        {
            _transferManager.ClearCompleted();
            var keepIds = _transferManager.GetItemsSnapshot().Select(x => x.Id).ToHashSet();
            for (int i = Transfers.Count - 1; i >= 0; i--)
            {
                if (!keepIds.Contains(Transfers[i].Model.Id))
                {
                    _transferMap.Remove(Transfers[i].Model.Id);
                    Transfers.RemoveAt(i);
                }
            }
            SavePendingQueueSafe();
        });
        ClearAllTransfersCommand = new RelayCommand(() =>
        {
            _transferManager.ClearAll();
            _transferMap.Clear();
            Transfers.Clear();
            SavePendingQueueSafe();
        });
        SaveSettingsCommand = new RelayCommand(() => { _settings.Save(); AppendLog("Settings saved."); });
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(silentIfUpToDate: false), () => !IsCheckingForUpdates && !IsDownloadingUpdate);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => _pendingUpdate is not null && !IsDownloadingUpdate);
        DismissUpdateBannerCommand = new RelayCommand(() => HasAvailableUpdate = false);
        SendSharedTextCommand = new AsyncRelayCommand(SendSharedTextAsync, () => IsConnected && !string.IsNullOrWhiteSpace(ShareTextInput));
        DismissReceivedTextCommand = new RelayCommand(() => HasReceivedText = false);
        _updateStatusText = $"Current version {AppVersionText}";

        // Populate Local pane right away (Home folder, or Drives if Home is inaccessible)
        string startLocal = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LoadLocalDirectory(Directory.Exists(startLocal) ? startLocal : FileShareServer.DrivesVirtualPath);

        if (_settings.AutoStartServer)
        {
            try
            {
                _server.Start();
                IsServerRunning = true;
            }
            catch (Exception ex)
            {
                AppendLog($"Could not start share server on port {_settings.ListenPort}: {ex.Message}");
            }
        }

        // Refresh interfaces AFTER server starts so PrimaryAddressText displays the actual listening port (not :0)
        RefreshLocalInterfaces();

        try
        {
            _discovery.Start();
        }
        catch (Exception ex)
        {
            AppendLog($"Discovery warning: {ex.Message}");
        }

        AppendLog("SwiftBeam ready. Auto-discovering peers on Wi-Fi, Wired LAN, and Direct Ethernet cables.");

        if (_settings.StartWithSystem)
            AutoStartHelper.TrySetAutoStart(true);

        try
        {
            int restoredCount = _transferManager.LoadPendingQueue(AppSettings.GetPendingQueueFilePath());
            if (restoredCount > 0)
            {
                AppendLog($"Restored {restoredCount} unfinished transfer(s) as Paused from previous session.");
                ShowToast($"Restored {restoredCount} paused transfer(s) — click Resume to continue");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Queue restore warning: {ex.Message}");
        }

        if (_settings.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await CheckForUpdatesAsync(silentIfUpToDate: true);
            });
        }
    }

    // --- Local File Browser Logic ---
    public void LoadLocalDirectory(string? targetPath)
    {
        string raw = (targetPath ?? string.Empty).Trim();
        _allLocalEntries.Clear();
        LocalEmptyMessage = string.Empty;

        if (string.IsNullOrEmpty(raw) || raw.Equals(FileShareServer.DrivesVirtualPath, StringComparison.OrdinalIgnoreCase) || raw.Equals("This PC / Drives", StringComparison.OrdinalIgnoreCase))
        {
            LocalPath = FileShareServer.DrivesVirtualPath;
            foreach (var root in FileShareServer.EnumerateRoots())
                AddLocalEntry(root);
            ApplyLocalFilter();
            UpdateLocalSelectionCount();
            return;
        }

        try
        {
            string osPath = raw.Replace('/', Path.DirectorySeparatorChar);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && osPath.Length == 2 && osPath[1] == ':')
                osPath += Path.DirectorySeparatorChar;

            string full = Path.GetFullPath(osPath);
            if (!Directory.Exists(full))
            {
                LocalEmptyMessage = $"Folder not found: {full}";
                ApplyLocalFilter();
                return;
            }

            LocalPath = full;
            var di = new DirectoryInfo(full);

            foreach (var d in SafeEnumerate(() => di.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)))
            {
                try
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((d.Attributes & FileAttributes.System) != 0 && (d.Attributes & FileAttributes.Hidden) != 0) continue;
                    AddLocalEntry(new RemoteEntry(d.Name, d.FullName, true, 0, d.LastWriteTimeUtc));
                }
                catch { }
            }

            foreach (var f in SafeEnumerate(() => di.EnumerateFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)))
            {
                try
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((f.Attributes & FileAttributes.System) != 0 && (f.Attributes & FileAttributes.Hidden) != 0) continue;
                    AddLocalEntry(new RemoteEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTimeUtc));
                }
                catch { }
            }

            if (_allLocalEntries.Count == 0)
                LocalEmptyMessage = "This folder is empty.";
        }
        catch (Exception ex)
        {
            LocalEmptyMessage = $"Cannot open folder: {ex.Message}";
        }

        ApplyLocalFilter();
        UpdateLocalSelectionCount();
    }

    private void AddLocalEntry(RemoteEntry entry)
    {
        var vm = new RemoteEntryViewModel(entry);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemoteEntryViewModel.IsSelected))
                UpdateLocalSelectionCount();
        };
        _allLocalEntries.Add(vm);
    }

    private static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> provider)
    {
        try { return provider().ToList(); }
        catch { return []; }
    }

    public void OpenLocalEntry(RemoteEntryViewModel entry)
    {
        if (entry.IsDirectory)
        {
            LoadLocalDirectory(entry.RelativePath);
        }
        else
        {
            entry.IsSelected = !entry.IsSelected;
            UpdateLocalSelectionCount();
        }
    }

    private void GoUpLocalDirectory()
    {
        if (string.IsNullOrEmpty(LocalPath) || LocalPath == FileShareServer.DrivesVirtualPath) return;
        var parent = Directory.GetParent(LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null)
            LoadLocalDirectory(FileShareServer.DrivesVirtualPath);
        else
            LoadLocalDirectory(parent.FullName);
    }

    public void SortLocal(string column)
    {
        if (_localSortColumn.Equals(column, StringComparison.OrdinalIgnoreCase))
            _localSortAsc = !_localSortAsc;
        else
        {
            _localSortColumn = column;
            _localSortAsc = true;
        }
        OnPropertyChanged(nameof(LocalNameHeader));
        OnPropertyChanged(nameof(LocalTypeHeader));
        OnPropertyChanged(nameof(LocalModifiedHeader));
        OnPropertyChanged(nameof(LocalSizeHeader));
        ApplyLocalFilter();
    }

    public void SortRemote(string column)
    {
        if (_remoteSortColumn.Equals(column, StringComparison.OrdinalIgnoreCase))
            _remoteSortAsc = !_remoteSortAsc;
        else
        {
            _remoteSortColumn = column;
            _remoteSortAsc = true;
        }
        OnPropertyChanged(nameof(RemoteNameHeader));
        OnPropertyChanged(nameof(RemoteTypeHeader));
        OnPropertyChanged(nameof(RemoteModifiedHeader));
        OnPropertyChanged(nameof(RemoteSizeHeader));
        ApplyRemoteFilter();
    }

    private static IEnumerable<RemoteEntryViewModel> SortEntries(IEnumerable<RemoteEntryViewModel> source, string column, bool asc)
    {
        // Keep folders grouped ahead of files (standard RustDesk / File Explorer behavior)
        var dirs = source.Where(e => e.IsDirectory);
        var files = source.Where(e => !e.IsDirectory);

        IEnumerable<RemoteEntryViewModel> Order(IEnumerable<RemoteEntryViewModel> seq) => column switch
        {
            "Type" => asc
                ? seq.OrderBy(e => e.TypeLabel, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : seq.OrderByDescending(e => e.TypeLabel, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "Modified" => asc
                ? seq.OrderBy(e => e.Entry.LastModifiedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : seq.OrderByDescending(e => e.Entry.LastModifiedUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "Size" => asc
                ? seq.OrderBy(e => e.Entry.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : seq.OrderByDescending(e => e.Entry.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => asc
                ? seq.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : seq.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

        return Order(dirs).Concat(Order(files));
    }

    private void ApplyLocalFilter()
    {
        FilteredLocalEntries.Clear();
        string q = LocalSearchFilter.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allLocalEntries
            : _allLocalEntries.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in SortEntries(filtered, _localSortColumn, _localSortAsc))
            FilteredLocalEntries.Add(entry);
    }

    private void SetLocalSelectionAll(bool selected)
    {
        foreach (var item in FilteredLocalEntries) item.IsSelected = selected;
        UpdateLocalSelectionCount();
    }

    private void UpdateLocalSelectionCount()
    {
        var selected = _allLocalEntries.Where(x => x.IsSelected).ToList();
        int folders = selected.Count(x => x.IsDirectory);
        int files = selected.Count - folders;
        long bytes = selected.Where(x => !x.IsDirectory).Sum(x => x.Entry.Size);
        LocalSelectionText = selected.Count == 0
            ? $"{_allLocalEntries.Count} items"
            : (folders > 0 ? $"{files} file(s), {folders} folder(s)" : $"{files} file(s) ({TransferManager.FormatBytes(bytes)})");
    }

    // --- Server & Installer controls ---
    private async Task ToggleServerAsync()
    {
        if (IsServerRunning)
        {
            await _server.StopAsync();
            IsServerRunning = false;
        }
        else
        {
            try
            {
                _server.Options.Port = _settings.ListenPort;
                _server.Start();
                IsServerRunning = true;
                RefreshLocalInterfaces();
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to start server: {ex.Message}");
            }
        }
    }

    private async Task ToggleInstallerServerAsync()
    {
        if (IsInstallerServerRunning)
        {
            await _installerServer.StopAsync();
            IsInstallerServerRunning = false;
            UpdateInstallerStatusText();
        }
        else
        {
            try
            {
                _installerServer.Start();
                IsInstallerServerRunning = true;
                RefreshLocalInterfaces();
                UpdateInstallerStatusText();
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to start local download page: {ex.Message}");
            }
        }
    }

    public void SetInstallerBinariesDirectory(string folderPath)
    {
        _installerServer.RefreshArtifacts(folderPath);
        UpdateInstallerStatusText();
        AppendLog($"Updated installer binaries folder ({_installerServer.Artifacts.Summary}).");
    }

    private void UpdateInstallerStatusText()
    {
        string dlPart = _installerDownloadsCount > 0 ? $" • {_installerDownloadsCount} downloaded" : string.Empty;
        InstallerStatusText = $"{_installerServer.Artifacts.Summary}{dlPart}";
    }

    public void RefreshLocalInterfaces()
    {
        var list = NetworkInfo.GetActiveAddresses();
        Dispatcher.UIThread.Post(() =>
        {
            LocalInterfaces.Clear();
            foreach (var addr in list)
                LocalInterfaces.Add(addr);

            int displayPort = _server.ListeningPort > 0 ? _server.ListeningPort : _settings.ListenPort;
            var primary = list.FirstOrDefault();
            PrimaryAddressText = primary is not null
                ? $"{primary.LinkBadge}: {primary.DisplayAddress}:{displayPort}"
                : $"Port {displayPort} (no active network)";

            int httpPort = _installerServer.IsRunning ? _installerServer.ListeningPort : InstallerWebServer.DefaultHttpPort;
            InstallerUrls.Clear();
            var ipv4Addrs = list.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            foreach (var v4 in ipv4Addrs)
                InstallerUrls.Add($"http://{v4.Address}:{httpPort}  ({v4.LinkBadge} • {v4.InterfaceName})");

            var bestV4 = ipv4Addrs.FirstOrDefault();
            InstallerUrl = bestV4 is not null ? $"http://{bestV4.Address}:{httpPort}" : $"http://localhost:{httpPort}";
        });
    }

    private void OnPeersChanged(IReadOnlyList<PeerInfo> peers)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string? selectedId = SelectedPeer?.ServerId;
            DiscoveredPeers.Clear();
            foreach (var peer in peers)
                DiscoveredPeers.Add(new PeerItemViewModel(peer));

            RefreshDiscoveredPeersSavedFlags();
            OnPropertyChanged(nameof(HasNoDiscoveredPeers));

            if (selectedId is not null)
            {
                var stillThere = DiscoveredPeers.FirstOrDefault(p => p.ServerId == selectedId);
                _selectedPeer = stillThere;
                OnPropertyChanged(nameof(SelectedPeer));
            }
            else if (DiscoveredPeers.Count == 1 && string.IsNullOrWhiteSpace(ConnectHost) && !IsConnected)
            {
                // Auto-select the only discovered nearby computer so user can click Connect right away
                SelectNearbyPeerForConnect(DiscoveredPeers[0]);
            }
        });
    }

    private void RefreshDiscoveredPeersSavedFlags()
    {
        foreach (var peer in DiscoveredPeers)
            peer.IsSaved = IsPeerSavedWithPin(peer);
    }

    private bool IsPeerSavedWithPin(PeerItemViewModel peer)
    {
        foreach (var ep in peer.Endpoints)
        {
            var match = _settings.SavedPeers.FirstOrDefault(s =>
                s.Host.Equals(ep.Host, StringComparison.OrdinalIgnoreCase) &&
                s.Port == ep.Port &&
                !string.IsNullOrWhiteSpace(s.SavedPin));
            if (match is not null)
                return true;
        }

        return _settings.SavedPeers.Any(s =>
            !string.IsNullOrWhiteSpace(s.DeviceName) &&
            s.DeviceName.Equals(peer.DeviceName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(s.SavedPin));
    }

    public SavedPeerEntry? FindSavedPeerForTarget(string host, int port, string? deviceName = null)
    {
        string h = (host ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(h))
        {
            var exact = _settings.SavedPeers.FirstOrDefault(p =>
                p.Host.Equals(h, StringComparison.OrdinalIgnoreCase) && p.Port == port);
            if (exact is not null)
                return exact;
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var byName = _settings.SavedPeers.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.DeviceName) &&
                p.DeviceName.Equals(deviceName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(p.SavedPin));
            if (byName is not null)
                return byName;
        }

        return null;
    }

    public bool CurrentTargetHasSavedPin()
    {
        int port = int.TryParse(ConnectPort.Trim(), out int parsed) ? parsed : ProtocolConstants.DefaultTransferPort;
        var saved = FindSavedPeerForTarget(ConnectHost.Trim(), port, SelectedPeer?.DeviceName);
        if (saved is null || string.IsNullOrWhiteSpace(saved.SavedPin))
            return false;

        if (string.IsNullOrWhiteSpace(ConnectPin))
            ConnectPin = saved.SavedPin;
        return true;
    }

    public bool CurrentTargetRequiresPin() => SelectedPeer is null || SelectedPeer.RequiresPin;

    public void SelectNearbyPeerForConnect(PeerItemViewModel peer)
    {
        SelectedPeer = peer;
        ConnectTargetDisplayName = peer.DeviceName;
        ConnectionError = string.Empty;

        int port = SelectedPeerEndpoint?.Port ?? ProtocolConstants.DefaultTransferPort;
        var saved = FindSavedPeerForTarget(ConnectHost.Trim(), port, peer.DeviceName);
        if (saved is not null)
        {
            ConnectUseTls = saved.UseTls;
            ConnectPin = saved.SavedPin ?? string.Empty;
        }
        else
        {
            ConnectPin = string.Empty;
        }
    }

    public void SelectSavedPeerForConnect(SavedPeerEntry saved)
    {
        _selectedPeer = null;
        OnPropertyChanged(nameof(SelectedPeer));
        SelectedPeerEndpoints.Clear();
        _selectedPeerEndpoint = null;
        OnPropertyChanged(nameof(SelectedPeerEndpoint));
        OnPropertyChanged(nameof(HasMultipleEndpoints));

        ConnectHost = saved.Host;
        ConnectPort = saved.Port.ToString();
        ConnectUseTls = saved.UseTls;
        ConnectPin = saved.SavedPin ?? string.Empty;
        ConnectTargetDisplayName = string.IsNullOrWhiteSpace(saved.DeviceName)
            ? saved.Host
            : $"★ {saved.DeviceName}";
        ConnectionError = string.Empty;
    }

    public void PrepareManualTarget()
    {
        _selectedPeer = null;
        OnPropertyChanged(nameof(SelectedPeer));
        SelectedPeerEndpoints.Clear();
        _selectedPeerEndpoint = null;
        OnPropertyChanged(nameof(SelectedPeerEndpoint));
        OnPropertyChanged(nameof(HasMultipleEndpoints));

        string host = ConnectHost.Trim();
        ConnectTargetDisplayName = host;
        ConnectionError = string.Empty;

        int port = int.TryParse(ConnectPort.Trim(), out int parsed) ? parsed : ProtocolConstants.DefaultTransferPort;
        var saved = FindSavedPeerForTarget(host, port);
        if (saved is not null && !string.IsNullOrWhiteSpace(saved.SavedPin))
        {
            ConnectUseTls = saved.UseTls;
            ConnectPin = saved.SavedPin;
        }
        else
        {
            ConnectPin = string.Empty;
        }
    }

    public void SetConnectionError(string message) => ConnectionError = message;

    // --- Remote Connection & File Browser Logic ---
    public async Task<ConnectAttemptResult> TryConnectAsync()
    {
        ConnectionError = string.Empty;

        string host = ConnectHost.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            const string msg = "Select a nearby computer or enter an IP address.";
            ConnectionError = msg;
            return new ConnectAttemptResult(false, false, msg);
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        if (!int.TryParse(ConnectPort.Trim(), out int port) || port is < 1 or > 65535)
        {
            const string msg = "Invalid port number.";
            ConnectionError = msg;
            return new ConnectAttemptResult(false, false, msg);
        }

        var target = new ConnectionTarget(host, port, ConnectPin.Trim(), ConnectUseTls, _settings.DeviceName);

        try
        {
            IsConnecting = true;
            var meta = await _browseClient.ConnectAsync(target);
            _transferManager.ConfigureTarget(target);

            _lastConnectionTarget = target;
            _connectedPeer = meta;
            string encBadge = meta.TlsActive ? "TLS 1.3" : "Direct Plain";
            string linkBadge = SelectedPeerEndpoint?.LinkBadge ?? "Network";
            _connectedEndpointSummary = $"{host}:{port} • {linkBadge} • {encBadge}";
            ConnectTargetDisplayName = meta.DeviceName;
            IsConnected = true;

            // Remember this computer (and PIN if RememberPin is checked) for 1-click reconnect next time
            _settings.UpsertSavedPeer(meta.DeviceName, host, port, ConnectUseTls, RememberPin ? ConnectPin.Trim() : string.Empty);
            ReloadSavedPeersCollection();

            AppendLog($"Connected to {meta.DeviceName} ({meta.OsPlatform}) at {host}:{port} ({encBadge})");

            // Jump immediately to remote Home folder (or Drives / root) so files appear right away!
            string initialPath = meta.FullFilesystemAccess
                ? (!string.IsNullOrWhiteSpace(meta.HomePath) ? meta.HomePath : FileShareServer.DrivesVirtualPath)
                : string.Empty;

            await LoadRemoteDirectoryAsync(initialPath);
            return new ConnectAttemptResult(true, false, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            _connectedPeer = null;
            IsConnected = false;
            ConnectionError = ex.Message;
            AppendLog($"Connect auth rejected ({host}:{port}): {ex.Message}");
            return new ConnectAttemptResult(false, true, ex.Message);
        }
        catch (Exception ex)
        {
            _connectedPeer = null;
            IsConnected = false;
            ConnectionError = ex.Message;
            AppendLog($"Connect failed ({host}:{port}): {ex.Message}");
            return new ConnectAttemptResult(false, false, ex.Message);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task DisconnectPeerAsync()
    {
        await _browseClient.DisconnectAsync();
        _lastConnectionTarget = null;
        _connectedPeer = null;
        _connectedEndpointSummary = string.Empty;
        IsConnected = false;
        RemotePath = string.Empty;
        RemoteEmptyMessage = string.Empty;
        _allRemoteEntries.Clear();
        FilteredRemoteEntries.Clear();
        RemoteSelectionText = "Not connected";
        AppendLog("Disconnected from remote computer.");
    }

    public async Task OpenRemoteEntryAsync(RemoteEntryViewModel entry)
    {
        if (!IsConnected) return;
        if (entry.IsDirectory)
        {
            await LoadRemoteDirectoryAsync(entry.RelativePath);
        }
        else
        {
            entry.IsSelected = !entry.IsSelected;
            UpdateRemoteSelectionCount();
        }
    }

    private async Task GoUpRemoteDirectoryAsync()
    {
        if (!IsConnected || string.IsNullOrEmpty(RemotePath) || RemotePath == FileShareServer.DrivesVirtualPath)
            return;

        string clean = RemotePath.Replace('\\', '/');
        if (RemoteHasFullFs)
        {
            // At root "/" or "C:/" -> jump to Drives list
            if (clean == "/" || (clean.Length == 3 && clean[1] == ':' && clean[2] == '/') || (clean.Length == 2 && clean[1] == ':'))
            {
                await LoadRemoteDirectoryAsync(FileShareServer.DrivesVirtualPath);
                return;
            }

            clean = clean.TrimEnd('/');
            int idx = clean.LastIndexOf('/');
            if (idx <= 0)
            {
                await LoadRemoteDirectoryAsync(clean.StartsWith('/') ? "/" : FileShareServer.DrivesVirtualPath);
                return;
            }

            string parent = clean[..idx];
            if (parent.Length == 2 && parent[1] == ':') parent += "/";
            await LoadRemoteDirectoryAsync(parent);
        }
        else
        {
            int idx = clean.LastIndexOf('/');
            string parent = idx <= 0 ? string.Empty : clean[..idx];
            await LoadRemoteDirectoryAsync(parent);
        }
    }

    private async Task LoadRemoteDirectoryAsync(string relativePath)
    {
        if (!IsConnected) return;
        try
        {
            string target = (relativePath ?? string.Empty).Trim();
            if (target.Equals("Drives / Roots", StringComparison.OrdinalIgnoreCase))
                target = FileShareServer.DrivesVirtualPath;

            ListDirResponseMessage resp;
            try
            {
                resp = await _browseClient.ListDirectoryAsync(target, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException && _lastConnectionTarget is not null)
            {
                // Transparently reconnect browse socket if Wi-Fi/cable briefly dropped while idle
                await _browseClient.ConnectAsync(_lastConnectionTarget);
                resp = await _browseClient.ListDirectoryAsync(target, recursive: false);
            }
            if (!string.IsNullOrEmpty(resp.Error))
            {
                ConnectionError = resp.Error;
                RemoteEmptyMessage = resp.Error;
                return;
            }

            ConnectionError = string.Empty;
            RemotePath = resp.CurrentPath;
            _allRemoteEntries.Clear();
            foreach (var entry in resp.Entries)
            {
                var vm = new RemoteEntryViewModel(entry);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RemoteEntryViewModel.IsSelected))
                        UpdateRemoteSelectionCount();
                };
                _allRemoteEntries.Add(vm);
            }

            RemoteEmptyMessage = _allRemoteEntries.Count == 0
                ? "This folder is empty."
                : string.Empty;

            ApplyRemoteFilter();
            UpdateRemoteSelectionCount();
        }
        catch (Exception ex)
        {
            ConnectionError = $"Browse failed: {ex.Message}";
            AppendLog($"Browse error: {ex.Message}");
        }
    }

    private void ApplyRemoteFilter()
    {
        FilteredRemoteEntries.Clear();
        string q = RemoteSearchFilter.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allRemoteEntries
            : _allRemoteEntries.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in SortEntries(filtered, _remoteSortColumn, _remoteSortAsc))
            FilteredRemoteEntries.Add(entry);
    }

    // --- Saved Peers management ---
    private void ReloadSavedPeersCollection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            SavedPeers.Clear();
            foreach (var p in _settings.SavedPeers)
                SavedPeers.Add(p);
            OnPropertyChanged(nameof(HasSavedPeers));
            RefreshDiscoveredPeersSavedFlags();
        });
    }

    public void RemoveSavedPeer(SavedPeerEntry entry)
    {
        _settings.SavedPeers.RemoveAll(p => p.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) && p.Port == entry.Port);
        _settings.Save();
        ReloadSavedPeersCollection();
    }

    // --- Local & Remote File Actions (New Folder, Rename, Delete, Drag & Drop) ---
    public RemoteEntryViewModel? GetFirstSelectedLocalEntry() => _allLocalEntries.FirstOrDefault(x => x.IsSelected);
    public RemoteEntryViewModel? GetFirstSelectedRemoteEntry() => _allRemoteEntries.FirstOrDefault(x => x.IsSelected);
    public int GetSelectedLocalCount() => _allLocalEntries.Count(x => x.IsSelected);
    public int GetSelectedRemoteCount() => _allRemoteEntries.Count(x => x.IsSelected);
    public IReadOnlyList<string> GetSelectedLocalPaths() => _allLocalEntries.Where(x => x.IsSelected).Select(x => x.RelativePath).ToList();
    public IReadOnlyList<RemoteEntry> GetSelectedRemoteEntries() => _allRemoteEntries.Where(x => x.IsSelected).Select(x => x.Entry).ToList();

    public void SelectRangeLocal(RemoteEntryViewModel anchor, RemoteEntryViewModel target, bool value)
    {
        int i1 = FilteredLocalEntries.IndexOf(anchor);
        int i2 = FilteredLocalEntries.IndexOf(target);
        if (i1 < 0 || i2 < 0) return;
        int start = Math.Min(i1, i2);
        int end = Math.Max(i1, i2);
        for (int i = start; i <= end; i++)
            FilteredLocalEntries[i].IsSelected = value;
        UpdateLocalSelectionCount();
    }

    public void SelectRangeRemote(RemoteEntryViewModel anchor, RemoteEntryViewModel target, bool value)
    {
        int i1 = FilteredRemoteEntries.IndexOf(anchor);
        int i2 = FilteredRemoteEntries.IndexOf(target);
        if (i1 < 0 || i2 < 0) return;
        int start = Math.Min(i1, i2);
        int end = Math.Max(i1, i2);
        for (int i = start; i <= end; i++)
            FilteredRemoteEntries[i].IsSelected = value;
        UpdateRemoteSelectionCount();
    }

    public void CreateLocalFolder(string folderName)
    {
        if (string.IsNullOrEmpty(LocalPath) || LocalPath == FileShareServer.DrivesVirtualPath) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(LocalPath, folderName));
            LoadLocalDirectory(LocalPath);
        }
        catch (Exception ex) { ConnectionError = $"Create folder failed: {ex.Message}"; }
    }

    public void RenameLocalSelected(string newName)
    {
        var item = GetFirstSelectedLocalEntry();
        if (item is null) return;
        try
        {
            string? parent = Path.GetDirectoryName(item.RelativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent)) return;
            string dest = Path.Combine(parent, newName);
            if (item.IsDirectory) Directory.Move(item.RelativePath, dest);
            else File.Move(item.RelativePath, dest, overwrite: false);
            LoadLocalDirectory(LocalPath);
        }
        catch (Exception ex) { ConnectionError = $"Rename failed: {ex.Message}"; }
    }

    public void DeleteLocalSelected()
    {
        var items = _allLocalEntries.Where(x => x.IsSelected).ToList();
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory) Directory.Delete(item.RelativePath, recursive: true);
                else File.Delete(item.RelativePath);
            }
            catch (Exception ex) { ConnectionError = $"Delete failed ({item.Name}): {ex.Message}"; }
        }
        LoadLocalDirectory(LocalPath);
    }

    public async Task CreateRemoteFolderAsync(string folderName)
    {
        if (!IsConnected || RemotePath == FileShareServer.DrivesVirtualPath) return;
        try
        {
            string target = string.IsNullOrEmpty(RemotePath)
                ? folderName
                : (RemotePath.EndsWith('/') ? RemotePath + folderName : RemotePath + "/" + folderName);
            var resp = await _browseClient.CreateDirectoryAsync(target);
            if (!resp.Success) ConnectionError = resp.Error ?? "Could not create remote folder.";
            else await LoadRemoteDirectoryAsync(RemotePath);
        }
        catch (Exception ex) { ConnectionError = $"Create remote folder failed: {ex.Message}"; }
    }

    public async Task RenameRemoteSelectedAsync(string newName)
    {
        var item = GetFirstSelectedRemoteEntry();
        if (!IsConnected || item is null) return;
        try
        {
            var resp = await _browseClient.RenamePathAsync(item.RelativePath, newName);
            if (!resp.Success) ConnectionError = resp.Error ?? "Could not rename remote item.";
            else await LoadRemoteDirectoryAsync(RemotePath);
        }
        catch (Exception ex) { ConnectionError = $"Remote rename failed: {ex.Message}"; }
    }

    public async Task DeleteRemoteSelectedAsync()
    {
        var items = _allRemoteEntries.Where(x => x.IsSelected).ToList();
        if (!IsConnected || items.Count == 0) return;
        foreach (var item in items)
        {
            try
            {
                var resp = await _browseClient.DeletePathAsync(item.RelativePath);
                if (!resp.Success) ConnectionError = resp.Error ?? $"Could not delete {item.Name}.";
            }
            catch (Exception ex) { ConnectionError = $"Remote delete failed ({item.Name}): {ex.Message}"; }
        }
        await LoadRemoteDirectoryAsync(RemotePath);
    }

    public Task SendDroppedLocalPathsAsync(IEnumerable<string> localPaths) =>
        SendLocalPathsToRemoteAsync(localPaths, isDrop: true);

    public async Task SendLocalPathsToRemoteAsync(IEnumerable<string> localPaths, bool isDrop = false)
    {
        if (!IsConnected)
        {
            ConnectionError = "Connect to a remote computer first before sending files.";
            return;
        }
        if (IsRemoteReadOnly)
        {
            ConnectionError = "Remote share is Read-Only; uploads are disabled by the host.";
            return;
        }
        if (RemotePath == FileShareServer.DrivesVirtualPath)
        {
            ConnectionError = "Open a folder on the Remote computer (e.g. Downloads or Desktop) before sending files.";
            return;
        }

        var pathList = localPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (pathList.Count == 0)
            return;

        ConnectionError = string.Empty;
        try
        {
            var added = await _transferManager.EnqueuePushPathsAsync(_browseClient, pathList, RemotePath);
            if (added.Count > 0)
            {
                string verb = isDrop ? "Dropped" : "Queued";
                AppendLog($"{verb} {added.Count} file(s) → sending to {RemotePathInput} ({ParallelWorkers} parallel streams).");
                ShowToast($"Queued {added.Count} file(s) to send");
                SavePendingQueueSafe();
            }
        }
        catch (Exception ex)
        {
            ConnectionError = $"Send failed: {ex.Message}";
            AppendLog($"Send error: {ex.Message}");
        }
    }

    public async Task PullEntriesToLocalAsync(IReadOnlyList<RemoteEntry> entries)
    {
        if (!IsConnected || entries.Count == 0)
            return;

        string destFolder = (!string.IsNullOrEmpty(LocalPath) && LocalPath != FileShareServer.DrivesVirtualPath && Directory.Exists(LocalPath))
            ? LocalPath
            : DownloadDirectory;

        ConnectionError = string.Empty;
        try
        {
            Directory.CreateDirectory(destFolder);
            string baseRemote = RemotePath == FileShareServer.DrivesVirtualPath ? string.Empty : RemotePath;
            var added = await _transferManager.EnqueueEntriesAsync(_browseClient, entries, destFolder, baseRemote);
            if (added.Count > 0)
            {
                AppendLog($"Pulling {added.Count} file(s) into {destFolder} ({ParallelWorkers} parallel streams).");
                ShowToast($"Queued {added.Count} file(s) to pull");
                SavePendingQueueSafe();
            }
        }
        catch (Exception ex)
        {
            ConnectionError = $"Pull failed: {ex.Message}";
            AppendLog($"Pull error: {ex.Message}");
        }
    }

    public void ShowToast(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _toastCts?.Cancel();
            _toastCts?.Dispose();
            var cts = new CancellationTokenSource();
            _toastCts = cts;

            ToastMessage = message;
            IsToastVisible = true;

            try
            {
                await Task.Delay(4500, cts.Token);
                IsToastVisible = false;
            }
            catch (OperationCanceledException) { }
        });
    }

    private void SetRemoteSelectionAll(bool selected)
    {
        foreach (var item in FilteredRemoteEntries) item.IsSelected = selected;
        UpdateRemoteSelectionCount();
    }

    private void UpdateRemoteSelectionCount()
    {
        var selected = _allRemoteEntries.Where(x => x.IsSelected).ToList();
        int folders = selected.Count(x => x.IsDirectory);
        int files = selected.Count - folders;
        long bytes = selected.Where(x => !x.IsDirectory).Sum(x => x.Entry.Size);
        RemoteSelectionText = selected.Count == 0
            ? $"{_allRemoteEntries.Count} items"
            : (folders > 0 ? $"{files} file(s), {folders} folder(s)" : $"{files} file(s) ({TransferManager.FormatBytes(bytes)})");
    }

    // --- Two-Way Transfers: Pull (Remote -> Local) & Send (Local -> Remote) ---
    private async Task PullSelectedToLocalAsync()
    {
        if (!IsConnected) return;
        var selected = _allRemoteEntries.Where(x => x.IsSelected).Select(x => x.Entry).ToList();
        if (selected.Count == 0)
        {
            ConnectionError = "Check one or more files/folders on the Remote side to pull.";
            return;
        }

        await PullEntriesToLocalAsync(selected);
        SetRemoteSelectionAll(false);
    }

    private async Task SendSelectedToRemoteAsync()
    {
        if (!IsConnected) return;
        var selected = _allLocalEntries.Where(x => x.IsSelected).Select(x => x.Entry.RelativePath).ToList();
        if (selected.Count == 0)
        {
            ConnectionError = "Check one or more files/folders on This Computer to send.";
            return;
        }

        await SendLocalPathsToRemoteAsync(selected, isDrop: false);
        if (string.IsNullOrEmpty(ConnectionError))
            SetLocalSelectionAll(false);
    }

    private async Task SendSharedTextAsync()
    {
        string text = ShareTextInput;
        if (!IsConnected || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var ack = await _browseClient.SendTextAsync(text, _settings.DeviceName);
            if (!ack.Success)
            {
                ConnectionError = ack.Error ?? "Could not deliver text snippet.";
                return;
            }
            ShareTextInput = string.Empty;
            AppendLog($"Sent text snippet ({text.Length} chars) to {_connectedPeer?.DeviceName ?? "peer"}.");
            ShowToast("Text snippet sent");
        }
        catch (Exception ex)
        {
            ConnectionError = $"Text send failed: {ex.Message}";
            AppendLog($"Text send error: {ex.Message}");
        }
    }

    private void OnServerTextReceived(string senderName, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReceivedTextSender = string.IsNullOrWhiteSpace(senderName) ? "Remote Peer" : senderName;
            ReceivedTextContent = text;
            HasReceivedText = true;
            AppendLog($"Received text snippet from {ReceivedTextSender} ({text.Length} chars).");
            ShowToast($"Text received from {ReceivedTextSender}");
        });
    }

    private void SavePendingQueueSafe()
    {
        try
        {
            _transferManager.SavePendingQueue(AppSettings.GetPendingQueueFilePath());
        }
        catch
        {
            // Ignore transient persistence write issues
        }
    }

    // --- Transfer Queue Events ---
    private void OnTransferItemAdded(TransferItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_transferMap.ContainsKey(item.Id)) return;
            var vm = new TransferItemViewModel(item, _transferManager);
            _transferMap[item.Id] = vm;
            Transfers.Insert(0, vm);
        });
    }

    private void OnTransferItemUpdated(TransferItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_transferMap.TryGetValue(item.Id, out var vm))
                vm.Refresh();

            // Debounced auto-refresh of destination pane when transfers finish (prevents UI thrashing on large batches)
            if (item.Status == TransferStatus.Completed)
            {
                var now = DateTime.UtcNow;
                bool anyStillActive = Transfers.Any(t => t.Model.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Verifying);
                if (!anyStillActive || (now - _lastAutoRefreshUtc).TotalSeconds >= 1.5)
                {
                    _lastAutoRefreshUtc = now;
                    if (item.Direction == TransferDirection.Pull && !_allLocalEntries.Any(e => e.IsSelected))
                        LoadLocalDirectory(LocalPath);
                    else if (item.Direction == TransferDirection.Push && IsConnected && !_allRemoteEntries.Any(e => e.IsSelected))
                        _ = LoadRemoteDirectoryAsync(RemotePath);
                }
            }
        });
    }

    private void OnTransferItemRemoved(TransferItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_transferMap.Remove(item.Id, out var vm))
                Transfers.Remove(vm);
        });
    }

    private void OnIncomingActivityChanged(IReadOnlyList<IncomingPeerActivity> peers)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool wasConnected = _hasIncomingPeer;
            if (peers.Count == 0)
            {
                HasIncomingPeer = false;
                IncomingPeerBadge = "PEER CONNECTED";
                IncomingPeerSummary = string.Empty;
                TrayTooltipText = $"SwiftBeam ({_settings.DeviceName})";
                return;
            }

            var primary = peers[0];
            bool activelyTransferring = peers.Any(p =>
                p.CurrentAction.StartsWith("Sending ", StringComparison.Ordinal) ||
                p.CurrentAction.StartsWith("Receiving ", StringComparison.Ordinal));

            IncomingPeerBadge = activelyTransferring ? "INCOMING TRANSFER" : "PEER CONNECTED";

            string rateText = primary.BytesPerSec > 1024
                ? $" • {TransferManager.FormatBytes((long)primary.BytesPerSec)}/s"
                : string.Empty;
            string totalText = primary.TotalBytesTransferred > 0
                ? $" • {TransferManager.FormatBytes(primary.TotalBytesTransferred)}"
                : string.Empty;
            string extraPeers = peers.Count > 1 ? $" (+{peers.Count - 1} more)" : string.Empty;
            string enc = primary.TlsActive ? "TLS" : "Direct";

            IncomingPeerSummary = $"{primary.ClientName} ({primary.RemoteAddress}){extraPeers} • {primary.CurrentAction} • {primary.ActiveStreams} stream(s){rateText}{totalText} • {enc}";
            HasIncomingPeer = true;
            TrayTooltipText = $"SwiftBeam ({_settings.DeviceName}) — {primary.ClientName}: {primary.CurrentAction}";

            if (!wasConnected)
            {
                ShowToast($"{primary.ClientName} connected to this computer");
            }
        });
    }

    private void OnTelemetryUpdated(TransferTelemetrySnapshot snap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OverallProgressPercent = snap.OverallProgressPercent;
            AggregateSpeedText = $"{TransferManager.FormatBytes((long)snap.AggregateBytesPerSec)}/s";
            ActiveWorkersText = $"{snap.ActiveWorkers} / {ParallelWorkers} streams";
            EtaText = snap.EstimatedTimeRemaining is { } eta
                ? (eta.TotalHours >= 1 ? eta.ToString(@"hh\:mm\:ss") : eta.ToString(@"mm\:ss"))
                : "--:--";

            OverallProgressText = snap.TotalBytesQueued > 0
                ? $"{snap.OverallProgressPercent:0.0}% • {TransferManager.FormatBytes(snap.TotalBytesTransferred)} / {TransferManager.FormatBytes(snap.TotalBytesQueued)} • {snap.CompletedFiles} done, {snap.QueuedOrActiveFiles} active"
                : "Ready — select files on either side to Send or Pull in parallel";

            // Detect batch completion (active dropped from >0 to 0 with new completed files)
            if (_prevActiveTransferCount > 0 && snap.QueuedOrActiveFiles == 0 && snap.CompletedFiles > _prevCompletedTransferCount)
            {
                if (_settings.NotifyOnTransferComplete)
                {
                    int delta = snap.CompletedFiles - _prevCompletedTransferCount;
                    ShowToast($"Transfer complete — {delta} file(s) finished ({TransferManager.FormatBytes(snap.TotalBytesTransferred)})");
                }
            }
            _prevActiveTransferCount = snap.QueuedOrActiveFiles;
            _prevCompletedTransferCount = snap.CompletedFiles;

            _sleepInhibitor.SetActive(PreventSleepDuringTransfers && snap.QueuedOrActiveFiles > 0);

            foreach (var vm in Transfers)
            {
                if (vm.Model.Status is TransferStatus.Transferring or TransferStatus.Reconnecting or TransferStatus.Verifying)
                    vm.Refresh();
            }
        });
    }

    private void RefreshAllTransferItemsUi()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var vm in Transfers) vm.Refresh();
        });
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Insert(0, line);
            while (LogEntries.Count > 300)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    // --- GitHub Releases Self-Update ---
    private async Task CheckForUpdatesAsync(bool silentIfUpToDate)
    {
        if (IsCheckingForUpdates || IsDownloadingUpdate)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsCheckingForUpdates = true;
            if (!silentIfUpToDate)
                UpdateStatusText = "Checking GitHub Releases...";
        });

        try
        {
            var current = GitHubUpdateService.GetCurrentVersion();
            var release = await _updateService.CheckForUpdateAsync(_settings.UpdateRepo, current);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (release is not null)
                {
                    _pendingUpdate = release;
                    string sizePart = release.AssetSizeBytes > 0
                        ? $" ({release.AssetSizeBytes / (1024.0 * 1024.0):0.0} MB)"
                        : string.Empty;
                    AvailableUpdateText = $"SwiftBeam v{release.Version} is available{sizePart}";
                    UpdateStatusText = $"Update v{release.Version} ready ({release.AssetName})";
                    HasAvailableUpdate = true;
                    InstallUpdateCommand.RaiseCanExecuteChanged();
                    AppendLog($"Update available: v{release.Version} ({release.DownloadUrl})");
                }
                else if (!silentIfUpToDate)
                {
                    UpdateStatusText = $"You are up to date ({AppVersionText}).";
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!silentIfUpToDate)
                {
                    UpdateStatusText = $"Update check failed: {ex.Message}";
                    AppendLog($"Update check error: {ex.Message}");
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsCheckingForUpdates = false);
        }
    }

    private async Task InstallUpdateAsync()
    {
        var release = _pendingUpdate;
        if (release is null || IsDownloadingUpdate)
            return;

        try
        {
            IsDownloadingUpdate = true;
            UpdateDownloadProgress = 0;
            UpdateStatusText = $"Downloading v{release.Version}...";

            var progress = new Progress<double>(pct =>
                Dispatcher.UIThread.Post(() => UpdateDownloadProgress = pct));

            string downloadedPath = await _updateService.DownloadUpdateAsync(release, progress);
            UpdateStatusText = "Verified SHA-256 — launching updater & restarting...";
            AppendLog($"Downloaded update v{release.Version} to {downloadedPath}");

            GitHubUpdateService.LaunchUpdateAndExit(
                downloadedPath,
                () => Dispatcher.UIThread.Post(() => RequestAppShutdown?.Invoke()));
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            ConnectionError = $"Update failed: {ex.Message}";
            UpdateStatusText = $"Update failed: {ex.Message}";
            AppendLog($"Update failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        SavePendingQueueSafe();
        _sleepInhibitor.Dispose();
        await _transferManager.DisposeAsync();
        await _browseClient.DisposeAsync();
        await _discovery.DisposeAsync();
        await _installerServer.DisposeAsync();
        await _server.DisposeAsync();
    }
}
