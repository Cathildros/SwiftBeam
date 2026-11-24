using Avalonia.Media;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Discovery;
using SwiftBeam.Core.Protocol;

namespace SwiftBeam.Desktop.ViewModels;

public static class FileIcons
{
    // Material-style crisp 24x24 vector icon paths (zero font dependencies across Windows & Linux)
    public static readonly Geometry Folder = Geometry.Parse(
        "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z");

    public static readonly Geometry Drive = Geometry.Parse(
        "M6,2H18A2,2 0 0,1 20,4V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V4A2,2 0 0,1 6,2M6,4V16H18V4H6M6,18V20H18V18H6M8,19A1,1 0 0,1 9,18A1,1 0 0,1 10,19A1,1 0 0,1 9,20A1,1 0 0,1 8,19Z");

    public static readonly Geometry Document = Geometry.Parse(
        "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M8,12H16V14H8V12M8,16H14V18H8V16Z");

    public static readonly Geometry GenericFile = Geometry.Parse(
        "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z");

    public static readonly Geometry Image = Geometry.Parse(
        "M19,19H5V5H19M19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M13.96,12.29L11.21,15.83L9.25,13.47L6.5,17H17.5L13.96,12.29Z");

    public static readonly Geometry Video = Geometry.Parse(
        "M18,4L20,8H17L15,4H13L15,8H12L10,4H8L10,8H7L5,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V4H18Z");

    public static readonly Geometry Audio = Geometry.Parse(
        "M12,3V13.55C11.41,13.21 10.73,13 10,13A4,4 0 0,0 6,17A4,4 0 0,0 10,21A4,4 0 0,0 14,17V7H18V3H12Z");

    public static readonly Geometry Archive = Geometry.Parse(
        "M20,6H12L10,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8A2,2 0 0,0 20,6M14,10H12V12H14V14H12V16H10V14H12V12H10V10H12V8H14V10Z");

    public static readonly Geometry Executable = Geometry.Parse(
        "M20,19V7H4V19H20M20,3A2,2 0 0,1 22,5V19A2,2 0 0,1 20,21H4A2,2 0 0,1 2,19V5C2,3.89 2.9,3 4,3H20M13,17V15H18V17H13M9.58,13L5.57,9H8.4L11.7,12.3C12.09,12.69 12.09,13.33 11.7,13.72L8.42,17H5.59L9.58,13Z");

    public static readonly Geometry Code = Geometry.Parse(
        "M14.6,16.6L19.2,12L14.6,7.4L16,6L22,12L16,18L14.6,16.6M9.4,16.6L4.8,12L9.4,7.4L8,6L2,12L8,18L9.4,16.6Z");

    public static readonly IBrush BrushFolder = SolidColorBrush.Parse("#FBBF24");
    public static readonly IBrush BrushDrive = SolidColorBrush.Parse("#A78BFA");
    public static readonly IBrush BrushImage = SolidColorBrush.Parse("#34D399");
    public static readonly IBrush BrushVideo = SolidColorBrush.Parse("#C084FC");
    public static readonly IBrush BrushAudio = SolidColorBrush.Parse("#F472B6");
    public static readonly IBrush BrushArchive = SolidColorBrush.Parse("#FB923C");
    public static readonly IBrush BrushExec = SolidColorBrush.Parse("#60A5FA");
    public static readonly IBrush BrushCode = SolidColorBrush.Parse("#22D3EE");
    public static readonly IBrush BrushDoc = SolidColorBrush.Parse("#CBD5E1");
    public static readonly IBrush BrushGeneric = SolidColorBrush.Parse("#94A3B8");

    public static (Geometry Icon, IBrush Brush, string TypeLabel) Classify(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            string rel = entry.RelativePath;
            bool isDrive = rel == "/" ||
                           (rel.Length <= 3 && rel.Length >= 2 && rel[1] == ':');
            return isDrive
                ? (Drive, BrushDrive, "Drive")
                : (Folder, BrushFolder, "Folder");
        }

        string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        string cleanExt = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "File";
        if (cleanExt.Length > 6) cleanExt = cleanExt[..6];

        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic"
                => (Image, BrushImage, cleanExt),

            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v"
                => (Video, BrushVideo, cleanExt),

            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma" or ".opus"
                => (Audio, BrushAudio, cleanExt),

            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".iso" or ".img" or ".nsp" or ".xci" or ".nca"
                => (Archive, BrushArchive, cleanExt),

            ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".appimage" or ".deb" or ".rpm" or ".dll" or ".so" or ".bin"
                => (Executable, BrushExec, cleanExt),

            ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".json" or ".xml" or ".yaml" or ".yml" or ".toml"
                or ".html" or ".css" or ".scss" or ".c" or ".cpp" or ".h" or ".hpp" or ".rs" or ".go" or ".java" or ".kt" or ".sql" or ".axaml" or ".xaml"
                => (Code, BrushCode, cleanExt),

            ".pdf" or ".doc" or ".docx" or ".txt" or ".md" or ".rtf" or ".csv" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".log" or ".ini" or ".cfg"
                => (Document, BrushDoc, cleanExt),

            _ => (GenericFile, BrushGeneric, cleanExt)
        };
    }
}

public sealed class PeerItemViewModel : ObservableObject
{
    private static readonly IBrush BrushSaved = SolidColorBrush.Parse("#FBBF24");
    private static readonly IBrush BrushPin = SolidColorBrush.Parse("#94A3B8");
    private static readonly IBrush BrushOpen = SolidColorBrush.Parse("#34D399");

    private bool _isSaved;

    public PeerInfo Model { get; }
    public string ServerId => Model.ServerId;
    public string DeviceName => Model.DeviceName;
    public string OsPlatform => Model.OsPlatform;
    public string PrimaryEndpointLabel => Model.BestEndpoint?.DisplayLabel ?? "Unknown";
    public string LinkBadge => Model.BestEndpoint?.LinkBadge ?? "LAN";
    public bool IsDirectCable => Model.HasDirectCable;
    public bool RequiresPin => Model.RequiresPin;
    public string PinBadge => Model.RequiresPin ? "PIN" : "Open";

    public bool IsSaved
    {
        get => _isSaved;
        set
        {
            if (SetProperty(ref _isSaved, value))
            {
                OnPropertyChanged(nameof(StatusBadge));
                OnPropertyChanged(nameof(StatusBadgeBrush));
            }
        }
    }

    public string StatusBadge => IsSaved ? "★ Saved" : (RequiresPin ? "PIN" : "Open");
    public IBrush StatusBadgeBrush => IsSaved ? BrushSaved : (RequiresPin ? BrushPin : BrushOpen);

    public string SecondaryLinksText
    {
        get
        {
            var eps = Model.OrderedEndpoints;
            if (eps.Count <= 1) return string.Empty;
            return "+ " + string.Join(", ", eps.Skip(1).Select(e => e.LinkBadge));
        }
    }

    public IReadOnlyList<PeerEndpoint> Endpoints => Model.OrderedEndpoints;

    public PeerItemViewModel(PeerInfo model)
    {
        Model = model;
    }
}

public sealed class RemoteEntryViewModel : ObservableObject
{
    private bool _isSelected;

    public RemoteEntry Entry { get; }
    public string Name => Entry.Name;
    public string RelativePath => Entry.RelativePath;
    public bool IsDirectory => Entry.IsDirectory;
    public Geometry IconData { get; }
    public IBrush IconBrush { get; }
    public string TypeLabel { get; }
    public string SizeText => Entry.IsDirectory
        ? (Entry.Size > 0 ? TransferManager.FormatBytes(Entry.Size) : string.Empty)
        : TransferManager.FormatBytes(Entry.Size);
    public string ModifiedText => Entry.IsDirectory && Entry.Size > 0
        ? string.Empty
        : Entry.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RemoteEntryViewModel(RemoteEntry entry)
    {
        Entry = entry;
        var (icon, brush, label) = FileIcons.Classify(entry);
        IconData = icon;
        IconBrush = brush;
        TypeLabel = label;
    }
}

public sealed class TransferItemViewModel : ObservableObject
{
    private double _progressPercent;
    private string _statusText = "Queued";
    private string _sizeProgressText = string.Empty;
    private string _speedText = string.Empty;
    private string _streamsText = string.Empty;
    private bool _canPause;
    private bool _canResume;
    private bool _canCancel;
    private bool _canOpenFolder;
    private IBrush _statusBrush = Brushes.LightSlateGray;

    private static readonly IBrush BrushBlue = SolidColorBrush.Parse("#A78BFA");
    private static readonly IBrush BrushPurple = SolidColorBrush.Parse("#E879F9");
    private static readonly IBrush BrushAmber = SolidColorBrush.Parse("#F59E0B");
    private static readonly IBrush BrushSlate = SolidColorBrush.Parse("#94A3B8");
    private static readonly IBrush BrushGreen = SolidColorBrush.Parse("#10B981");
    private static readonly IBrush BrushMint = SolidColorBrush.Parse("#34D399");
    private static readonly IBrush BrushRed = SolidColorBrush.Parse("#EF4444");
    private static readonly IBrush BrushMuted = SolidColorBrush.Parse("#64748B");
    private static readonly IBrush BrushLight = SolidColorBrush.Parse("#CBD5E1");

    public TransferItem Model { get; }
    public string FileName => Model.FileName;
    public string RelativePath => Model.RemoteRelativePath;
    public string DirectionBadge => Model.Direction == TransferDirection.Push ? "↑ SEND" : "↓ PULL";
    public IBrush DirectionBrush => Model.Direction == TransferDirection.Push ? BrushGreen : BrushBlue;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SizeProgressText
    {
        get => _sizeProgressText;
        private set => SetProperty(ref _sizeProgressText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string StreamsText
    {
        get => _streamsText;
        private set => SetProperty(ref _streamsText, value);
    }

    public bool CanPause
    {
        get => _canPause;
        private set => SetProperty(ref _canPause, value);
    }

    public bool CanResume
    {
        get => _canResume;
        private set => SetProperty(ref _canResume, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set => SetProperty(ref _canCancel, value);
    }

    public bool CanOpenFolder
    {
        get => _canOpenFolder;
        private set => SetProperty(ref _canOpenFolder, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }

    public TransferItemViewModel(TransferItem model, TransferManager manager)
    {
        Model = model;
        PauseCommand = new RelayCommand(() => manager.PauseItem(model));
        ResumeCommand = new RelayCommand(() => manager.ResumeItem(model));
        CancelCommand = new RelayCommand(() => manager.CancelItem(model, deletePartialFiles: true));
        Refresh();
    }

    public void Refresh()
    {
        ProgressPercent = Model.ProgressPercent;
        SizeProgressText = $"{TransferManager.FormatBytes(Model.BytesTransferred)} / {TransferManager.FormatBytes(Model.TotalBytes)}";

        StatusText = string.IsNullOrWhiteSpace(Model.StatusDetail)
            ? Model.Status.ToString()
            : $"{Model.Status}: {Model.StatusDetail}";

        SpeedText = Model.Status == TransferStatus.Transferring && Model.CurrentSpeedBytesPerSec > 0
            ? $"{TransferManager.FormatBytes((long)Model.CurrentSpeedBytesPerSec)}/s"
            : string.Empty;

        StreamsText = Model.ActiveStreams > 0
            ? $"{Model.ActiveStreams}x"
            : string.Empty;

        CanPause = Model.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting;
        CanResume = Model.Status is TransferStatus.Paused or TransferStatus.Failed;
        CanCancel = Model.Status is not (TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Cancelled);
        CanOpenFolder = Model.Direction == TransferDirection.Pull && Model.Status is TransferStatus.Completed or TransferStatus.Skipped;

        StatusBrush = Model.Status switch
        {
            TransferStatus.Transferring => BrushBlue,
            TransferStatus.Verifying => BrushPurple,
            TransferStatus.Reconnecting => BrushAmber,
            TransferStatus.Paused => BrushSlate,
            TransferStatus.Completed => BrushGreen,
            TransferStatus.Skipped => BrushMint,
            TransferStatus.Failed => BrushRed,
            TransferStatus.Cancelled => BrushMuted,
            _ => BrushLight
        };
    }
}
