using System.Text.Json.Serialization;

namespace SwiftBeam.Core.Protocol;

public enum FrameType : byte
{
    AuthChallenge = 0x01,
    AuthResponse = 0x02,
    AuthResult = 0x03,

    ListDirRequest = 0x10,
    ListDirResponse = 0x11,
    FileInfoRequest = 0x12,
    FileInfoResponse = 0x13,
    CreateDirRequest = 0x14,
    CreateDirResponse = 0x15,
    DeletePathRequest = 0x16,
    DeletePathResponse = 0x17,
    RenamePathRequest = 0x18,
    RenamePathResponse = 0x19,

    PullRangeRequest = 0x20,
    PullRangeHeader = 0x21,
    DataChunk = 0x22,
    DataEnd = 0x23,
    PushChunkRequest = 0x24,
    PushChunkAck = 0x25,
    PushFinalizeRequest = 0x26,
    PushFinalizeAck = 0x27,

    Ping = 0x30,
    Pong = 0x31,

    Error = 0xFF
}

public static class ProtocolConstants
{
    public const int DefaultTransferPort = 45890;
    public const int DefaultDiscoveryPort = 45891;
    public const int MaxControlFrameBytes = 16 * 1024 * 1024; // 16 MB (large directory listings)
    public const int DataFrameChunkBytes = 1024 * 1024;       // 1 MB per binary frame for high throughput
    public const int SocketBufferBytes = 4 * 1024 * 1024;     // 4 MB TCP send/receive window for high-BDP links

    public static ReadOnlySpan<byte> MagicPlain => "SBM1"u8;
    public static ReadOnlySpan<byte> MagicTls => "SBT1"u8;

    public const byte PreambleOk = 0x01;
    public const byte PreambleTlsRequired = 0x02;
    public const byte PreambleInvalid = 0x03;
}

public sealed record AuthChallengeMessage(
    string ServerId,
    string DeviceName,
    string OsPlatform,
    string ServerNonceB64,
    bool RequiresPin,
    bool TlsActive,
    string? CertFingerprint);

public sealed record AuthResponseMessage(
    string ClientName,
    string ClientNonceB64,
    string ProofB64);

public sealed record AuthResultMessage(
    bool Success,
    string? Error,
    string? SharedRootName,
    bool FullFilesystemAccess = false,
    string? HomePath = null,
    string? DesktopPath = null,
    string? DownloadsPath = null,
    string? DocumentsPath = null);

public sealed record CreateDirRequestMessage(string RelativePath);

public sealed record CreateDirResponseMessage(bool Success, string? Error = null);

public sealed record DeletePathRequestMessage(string RelativePath);

public sealed record DeletePathResponseMessage(bool Success, string? Error = null);

public sealed record RenamePathRequestMessage(string RelativePath, string NewName);

public sealed record RenamePathResponseMessage(bool Success, string? NewRelativePath = null, string? Error = null);

public sealed record PushChunkRequestMessage(
    string RelativePath,
    long TotalFileSize,
    long Offset,
    long Length,
    DateTime LastModifiedUtc);

public sealed record PushChunkAckMessage(
    bool Success,
    string? Error = null);

public sealed record PushFinalizeRequestMessage(
    string RelativePath,
    long TotalFileSize,
    DateTime LastModifiedUtc,
    string? ExpectedSha256Hex = null);

public sealed record PushFinalizeAckMessage(
    bool Success,
    string? Error = null);

public sealed record ListDirRequestMessage(
    string RelativePath,
    bool Recursive = false);

public sealed record RemoteEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTime LastModifiedUtc);

public sealed record ListDirResponseMessage(
    string CurrentPath,
    List<RemoteEntry> Entries,
    string? Error = null);

public sealed record FileInfoRequestMessage(
    string RelativePath,
    bool IncludeSha256 = false);

public sealed record FileInfoResponseMessage(
    bool Exists,
    long Size,
    DateTime LastModifiedUtc,
    string? Sha256Hex = null,
    string? Error = null);

public sealed record PullRangeRequestMessage(
    string RelativePath,
    long Offset,
    long Length);

public sealed record PullRangeHeaderMessage(
    bool Success,
    long FileSize,
    DateTime LastModifiedUtc,
    long Offset,
    long Length,
    string? Error = null);

public sealed record ErrorMessage(string Message);
