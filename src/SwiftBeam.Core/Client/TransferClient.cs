using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;

namespace SwiftBeam.Core.Client;

public sealed record ConnectionTarget(
    string Host,
    int Port,
    string Pin,
    bool UseTls,
    string ClientName);

public sealed record ConnectedPeerMetadata(
    string ServerId,
    string DeviceName,
    string OsPlatform,
    string SharedRootName,
    bool TlsActive,
    string? CertFingerprint,
    bool FullFilesystemAccess = false,
    string? HomePath = null,
    string? DesktopPath = null,
    string? DownloadsPath = null,
    string? DocumentsPath = null,
    bool ReadOnly = false);

public sealed class TransferClient : IAsyncDisposable
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private SslStream? _sslStream;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public bool IsConnected => _tcpClient is { Connected: true } && _stream is not null;
    public ConnectedPeerMetadata? PeerMetadata { get; private set; }

    public async Task<ConnectedPeerMetadata> ConnectAsync(ConnectionTarget target, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var tcp = new TcpClient
        {
            NoDelay = true,
            SendBufferSize = ProtocolConstants.SocketBufferBytes,
            ReceiveBufferSize = ProtocolConstants.SocketBufferBytes
        };

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));

            if (IPAddress.TryParse(target.Host, out var ip))
            {
                await tcp.ConnectAsync(ip, target.Port, connectCts.Token).ConfigureAwait(false);
            }
            else
            {
                await tcp.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false);
            }

            var rawStream = tcp.GetStream();

            // 1. Send preamble
            byte[] magic = target.UseTls ? ProtocolConstants.MagicTls.ToArray() : ProtocolConstants.MagicPlain.ToArray();
            await rawStream.WriteAsync(magic, ct).ConfigureAwait(false);
            await rawStream.FlushAsync(ct).ConfigureAwait(false);

            byte[] ack = new byte[1];
            await FrameProtocol.ReadExactAsync(rawStream, ack, ct).ConfigureAwait(false);
            if (ack[0] == ProtocolConstants.PreambleTlsRequired)
                throw new InvalidOperationException("Remote peer requires TLS encryption. Enable TLS and reconnect.");
            if (ack[0] != ProtocolConstants.PreambleOk)
                throw new InvalidDataException($"Remote peer rejected protocol preamble (code 0x{ack[0]:X2}).");

            Stream activeStream = rawStream;
            byte[]? remoteCertHash = null;

            if (target.UseTls)
            {
                var ssl = new SslStream(rawStream, leaveInnerStreamOpen: true);
                var sslOpts = TlsHelper.CreateClientOptions(target.Host, hash => remoteCertHash = hash);
                await ssl.AuthenticateAsClientAsync(sslOpts, ct).ConfigureAwait(false);
                _sslStream = ssl;
                activeStream = ssl;
            }

            // 2. Challenge-response PIN authentication
            var challenge = await FrameProtocol.ReadJsonFrameAsync<AuthChallengeMessage>(activeStream, FrameType.AuthChallenge, ct).ConfigureAwait(false);

            byte[] serverNonce = Convert.FromBase64String(challenge.ServerNonceB64);
            byte[] clientNonce = PinAuth.GenerateNonce();
            byte[] proof = challenge.RequiresPin
                ? PinAuth.ComputeProof(target.Pin, serverNonce, clientNonce, target.UseTls ? remoteCertHash : null)
                : Array.Empty<byte>();

            var response = new AuthResponseMessage(
                string.IsNullOrWhiteSpace(target.ClientName) ? Environment.MachineName : target.ClientName,
                Convert.ToBase64String(clientNonce),
                Convert.ToBase64String(proof));

            await FrameProtocol.WriteJsonFrameAsync(activeStream, FrameType.AuthResponse, response, ct).ConfigureAwait(false);

            var authResult = await FrameProtocol.ReadJsonFrameAsync<AuthResultMessage>(activeStream, FrameType.AuthResult, ct).ConfigureAwait(false);
            if (!authResult.Success)
                throw new UnauthorizedAccessException(authResult.Error ?? "Authentication failed.");

            _tcpClient = tcp;
            _stream = activeStream;
            PeerMetadata = new ConnectedPeerMetadata(
                challenge.ServerId,
                challenge.DeviceName,
                challenge.OsPlatform,
                authResult.SharedRootName ?? "Shared",
                challenge.TlsActive,
                challenge.CertFingerprint,
                authResult.FullFilesystemAccess,
                authResult.HomePath,
                authResult.DesktopPath,
                authResult.DownloadsPath,
                authResult.DocumentsPath,
                authResult.ReadOnly);

            return PeerMetadata;
        }
        catch
        {
            if (_sslStream is not null)
            {
                await _sslStream.DisposeAsync().ConfigureAwait(false);
                _sslStream = null;
            }
            tcp.Dispose();
            throw;
        }
    }

    public async Task<ListDirResponseMessage> ListDirectoryAsync(string relativePath, bool recursive = false, bool includeHidden = false, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.ListDirRequest, new ListDirRequestMessage(relativePath, recursive, includeHidden), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<ListDirResponseMessage>(stream, FrameType.ListDirResponse, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<FileInfoResponseMessage> GetFileInfoAsync(string relativePath, bool includeSha256 = false, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.FileInfoRequest, new FileInfoRequestMessage(relativePath, includeSha256), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<FileInfoResponseMessage>(stream, FrameType.FileInfoResponse, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<CreateDirResponseMessage> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.CreateDirRequest, new CreateDirRequestMessage(relativePath), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<CreateDirResponseMessage>(stream, FrameType.CreateDirResponse, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<DeletePathResponseMessage> DeletePathAsync(string relativePath, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.DeletePathRequest, new DeletePathRequestMessage(relativePath), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<DeletePathResponseMessage>(stream, FrameType.DeletePathResponse, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<RenamePathResponseMessage> RenamePathAsync(string relativePath, string newName, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.RenamePathRequest, new RenamePathRequestMessage(relativePath, newName), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<RenamePathResponseMessage>(stream, FrameType.RenamePathResponse, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<SendTextAckMessage> SendTextAsync(string text, string senderName, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.SendTextRequest, new SendTextRequestMessage(text, senderName), ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<SendTextAckMessage>(stream, FrameType.SendTextAck, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<PushChunkAckMessage> PushRangeAsync(
        string remoteRelativePath,
        string localSourcePath,
        long totalFileSize,
        DateTime lastModifiedUtc,
        long offset,
        long length,
        Action<int> onBytesSent,
        CancellationToken ct = default,
        bool allowCompression = false)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            bool allowCompress = allowCompression && CompressionHelper.ShouldCompressFile(localSourcePath);
            await FrameProtocol.WriteJsonFrameAsync(
                stream,
                FrameType.PushChunkRequest,
                new PushChunkRequestMessage(remoteRelativePath, totalFileSize, offset, length, lastModifiedUtc, allowCompress),
                ct).ConfigureAwait(false);

            if (length > 0)
            {
                using var handle = File.OpenHandle(localSourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int frameBufSize = 5 + ProtocolConstants.DataFrameChunkBytes;
                int compBufSize = 9 + ProtocolConstants.DataFrameChunkBytes;
                byte[] bufA = ArrayPool<byte>.Shared.Rent(frameBufSize);
                byte[] bufB = ArrayPool<byte>.Shared.Rent(frameBufSize);
                byte[]? compA = allowCompress ? ArrayPool<byte>.Shared.Rent(compBufSize) : null;
                byte[]? compB = allowCompress ? ArrayPool<byte>.Shared.Rent(compBufSize) : null;
                Task? pendingSend = null;
                try
                {
                    long remaining = length;
                    long pos = offset;
                    bool useA = true;

                    while (remaining > 0)
                    {
                        byte[] curBuf = useA ? bufA : bufB;
                        byte[]? curComp = useA ? compA : compB;
                        int toRead = (int)Math.Min(remaining, ProtocolConstants.DataFrameChunkBytes);
                        int read = await RandomAccess.ReadAsync(handle, curBuf.AsMemory(5, toRead), pos, ct).ConfigureAwait(false);
                        if (read == 0) break;

                        if (pendingSend is not null)
                            await pendingSend.ConfigureAwait(false);

                        if (curComp is not null && CompressionHelper.TryCompressPreframed(curBuf.AsSpan(5, read), curComp, out int compPacketBytes))
                        {
                            pendingSend = stream.WriteAsync(curComp.AsMemory(0, compPacketBytes), ct).AsTask();
                        }
                        else
                        {
                            pendingSend = FrameProtocol.WritePreframedDataAsync(stream, curBuf, read, ct).AsTask();
                        }
                        pos += read;
                        remaining -= read;
                        useA = !useA;
                        onBytesSent(read);
                    }

                    if (pendingSend is not null)
                        await pendingSend.ConfigureAwait(false);
                }
                finally
                {
                    if (pendingSend is not null)
                    {
                        try { await pendingSend.ConfigureAwait(false); } catch { }
                    }
                    ArrayPool<byte>.Shared.Return(bufA);
                    ArrayPool<byte>.Shared.Return(bufB);
                    if (compA is not null) ArrayPool<byte>.Shared.Return(compA);
                    if (compB is not null) ArrayPool<byte>.Shared.Return(compB);
                }
            }

            await FrameProtocol.WriteFrameAsync(stream, FrameType.DataEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<PushChunkAckMessage>(stream, FrameType.PushChunkAck, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<PushFinalizeAckMessage> FinalizePushAsync(
        string remoteRelativePath,
        long totalFileSize,
        DateTime lastModifiedUtc,
        string? expectedSha256Hex,
        CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteJsonFrameAsync(
                stream,
                FrameType.PushFinalizeRequest,
                new PushFinalizeRequestMessage(remoteRelativePath, totalFileSize, lastModifiedUtc, expectedSha256Hex),
                ct).ConfigureAwait(false);
            return await FrameProtocol.ReadJsonFrameAsync<PushFinalizeAckMessage>(stream, FrameType.PushFinalizeAck, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<PullRangeHeaderMessage> PullRangeAsync(
        string relativePath,
        long offset,
        long length,
        Func<ReadOnlyMemory<byte>, long, CancellationToken, ValueTask> onDataReceived,
        CancellationToken ct = default,
        Action<int>? onNetworkBytesProgress = null,
        bool allowCompression = false)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            bool allowCompress = allowCompression && CompressionHelper.ShouldCompressFile(relativePath);
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PullRangeRequest,
                new PullRangeRequestMessage(relativePath, offset, length, allowCompress), ct).ConfigureAwait(false);

            var header = await FrameProtocol.ReadJsonFrameAsync<PullRangeHeaderMessage>(stream, FrameType.PullRangeHeader, ct).ConfigureAwait(false);
            if (!header.Success)
                return header;

            byte[] bufA = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes);
            byte[] bufB = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes);
            byte[]? compBuf = allowCompress ? ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes + 1024) : null;
            Task? pendingWrite = null;
            try
            {
                long writePos = header.Offset;
                long received = 0;
                bool useA = true;

                while (true)
                {
                    var frameHeader = await FrameProtocol.ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Connection dropped mid-transfer.");

                    if (frameHeader.Type == FrameType.DataEnd)
                        break;

                    if (frameHeader.Type == FrameType.Error)
                    {
                        byte[] errPayload = new byte[frameHeader.Length];
                        if (errPayload.Length > 0)
                            await FrameProtocol.ReadExactAsync(stream, errPayload, ct).ConfigureAwait(false);
                        var err = FrameProtocol.DeserializePayload<ErrorMessage>(errPayload);
                        throw new InvalidOperationException(err.Message);
                    }

                    if (frameHeader.Type == FrameType.CompressedDataChunk)
                    {
                        compBuf ??= ArrayPool<byte>.Shared.Rent(Math.Max(ProtocolConstants.DataFrameChunkBytes + 1024, frameHeader.Length));
                        if (frameHeader.Length > compBuf.Length)
                        {
                            ArrayPool<byte>.Shared.Return(compBuf);
                            compBuf = ArrayPool<byte>.Shared.Rent(frameHeader.Length);
                        }

                        await FrameProtocol.ReadExactAsync(stream, compBuf.AsMemory(0, frameHeader.Length), ct).ConfigureAwait(false);
                        byte[] curBuf = useA ? bufA : bufB;
                        int rawLen = CompressionHelper.DecompressFramePayload(compBuf.AsSpan(0, frameHeader.Length), curBuf.AsSpan(0, ProtocolConstants.DataFrameChunkBytes));
                        onNetworkBytesProgress?.Invoke(rawLen);

                        if (pendingWrite is not null)
                            await pendingWrite.ConfigureAwait(false);

                        long targetOffset = writePos;
                        pendingWrite = onDataReceived(curBuf.AsMemory(0, rawLen), targetOffset, ct).AsTask();
                        writePos += rawLen;
                        received += rawLen;
                        useA = !useA;
                        continue;
                    }

                    if (frameHeader.Type != FrameType.DataChunk)
                        throw new InvalidDataException($"Unexpected frame {frameHeader.Type} during data stream.");

                    int remainingFrame = frameHeader.Length;
                    while (remainingFrame > 0)
                    {
                        byte[] curBuf = useA ? bufA : bufB;
                        int slice = Math.Min(remainingFrame, ProtocolConstants.DataFrameChunkBytes);
                        await FrameProtocol.ReadExactAsync(stream, curBuf.AsMemory(0, slice), onNetworkBytesProgress, ct).ConfigureAwait(false);

                        if (pendingWrite is not null)
                            await pendingWrite.ConfigureAwait(false);

                        long targetOffset = writePos;
                        pendingWrite = onDataReceived(curBuf.AsMemory(0, slice), targetOffset, ct).AsTask();
                        writePos += slice;
                        received += slice;
                        remainingFrame -= slice;
                        useA = !useA;
                    }
                }

                if (pendingWrite is not null)
                    await pendingWrite.ConfigureAwait(false);

                if (received != header.Length)
                    throw new IOException($"Short transfer: expected {header.Length} bytes, received {received} bytes.");

                return header;
            }
            finally
            {
                if (pendingWrite is not null)
                {
                    try { await pendingWrite.ConfigureAwait(false); } catch { }
                }
                ArrayPool<byte>.Shared.Return(bufA);
                ArrayPool<byte>.Shared.Return(bufB);
                if (compBuf is not null) ArrayPool<byte>.Shared.Return(compBuf);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<(byte[] Data, long TotalFileSize, bool Truncated)> ReadFilePreviewAsync(
        string remoteRelativePath,
        int maxBytes = 262_144,
        CancellationToken ct = default)
    {
        var info = await GetFileInfoAsync(remoteRelativePath, includeSha256: false, ct).ConfigureAwait(false);
        if (!info.Exists)
            throw new FileNotFoundException($"Remote file not found: {remoteRelativePath}");

        if (info.Size == 0)
            return ([], 0, false);

        int toRead = (int)Math.Min(info.Size, maxBytes);
        byte[] buffer = new byte[toRead];

        var header = await PullRangeAsync(
            remoteRelativePath,
            0,
            toRead,
            (chunk, writeOffset, _) =>
            {
                int destOffset = (int)writeOffset;
                if (destOffset >= 0 && destOffset + chunk.Length <= buffer.Length)
                    chunk.CopyTo(buffer.AsMemory(destOffset));
                return ValueTask.CompletedTask;
            },
            ct,
            allowCompression: true).ConfigureAwait(false);

        if (!header.Success)
            throw new IOException(header.Error ?? "Could not read remote file preview.");

        return (buffer, info.Size, info.Size > toRead);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = EnsureStream();
            await FrameProtocol.WriteFrameAsync(stream, FrameType.Ping, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            var resp = await FrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            return resp is { Type: FrameType.Pong };
        }
        catch
        {
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private Stream EnsureStream()
    {
        return _stream ?? throw new InvalidOperationException("Client is not connected.");
    }

    public async Task DisconnectAsync()
    {
        if (_sslStream is not null)
        {
            try { await _sslStream.DisposeAsync().ConfigureAwait(false); } catch { }
            _sslStream = null;
        }
        if (_tcpClient is not null)
        {
            try { _tcpClient.Dispose(); } catch { }
            _tcpClient = null;
        }
        _stream = null;
        PeerMetadata = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ioLock.Dispose();
    }
}
