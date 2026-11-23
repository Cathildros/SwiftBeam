using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace SwiftBeam.Core.Protocol;

public static class FrameProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteJsonFrameAsync<T>(Stream stream, FrameType type, T payload, CancellationToken ct)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await WriteFrameAsync(stream, type, jsonBytes, ct).ConfigureAwait(false);
    }

    public static async Task WriteFrameAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        int totalLen = 5 + payload.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            packet[0] = (byte)type;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), payload.Length);
            if (!payload.IsEmpty)
                payload.Span.CopyTo(packet.AsSpan(5));

            await stream.WriteAsync(packet.AsMemory(0, totalLen), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <summary>
    /// Writes a pre-framed data packet where bytes [0..5) are reserved for the frame header
    /// and bytes [5..5+payloadLength) hold the raw data — single zero-copy syscall with no 5-byte micro-packet!
    /// </summary>
    public static ValueTask WritePreframedDataAsync(Stream stream, byte[] bufferWithHeaderSlot, int payloadLength, CancellationToken ct)
    {
        bufferWithHeaderSlot[0] = (byte)FrameType.DataChunk;
        BinaryPrimitives.WriteInt32BigEndian(bufferWithHeaderSlot.AsSpan(1, 4), payloadLength);
        return stream.WriteAsync(bufferWithHeaderSlot.AsMemory(0, 5 + payloadLength), ct);
    }

    public static async Task WriteDataFrameUnflushedAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        int totalLen = 5 + payload.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            packet[0] = (byte)FrameType.DataChunk;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), payload.Length);
            if (!payload.IsEmpty)
                payload.Span.CopyTo(packet.AsSpan(5));
            await stream.WriteAsync(packet.AsMemory(0, totalLen), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public static async Task<(FrameType Type, int Length)?> ReadFrameHeaderAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[5];
        int totalRead = 0;
        while (totalRead < 5)
        {
            int n = await stream.ReadAsync(header.AsMemory(totalRead, 5 - totalRead), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (totalRead == 0) return null; // Clean EOF
                throw new EndOfStreamException("Connection closed mid-frame header.");
            }
            totalRead += n;
        }

        var type = (FrameType)header[0];
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > ProtocolConstants.MaxControlFrameBytes)
            throw new InvalidDataException($"Invalid frame length: {length}");

        return (type, length);
    }

    public static Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
        => ReadExactAsync(stream, buffer, onBytesRead: null, ct);

    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, Action<int>? onBytesRead, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Connection closed unexpectedly while reading frame payload.");
            offset += n;
            onBytesRead?.Invoke(n);
        }
    }

    public static async Task<(FrameType Type, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);
        if (header is null) return null;

        byte[] payload = header.Value.Length == 0 ? Array.Empty<byte>() : new byte[header.Value.Length];
        if (payload.Length > 0)
        {
            await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        }
        return (header.Value.Type, payload);
    }

    public static T DeserializePayload<T>(byte[] payload)
    {
        var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
        return result ?? throw new InvalidDataException($"Failed to deserialize {typeof(T).Name}.");
    }

    public static async Task<T> ReadJsonFrameAsync<T>(Stream stream, FrameType expectedType, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException($"Connection closed while waiting for {expectedType}.");

        if (frame.Type == FrameType.Error)
        {
            var err = DeserializePayload<ErrorMessage>(frame.Payload);
            throw new InvalidOperationException($"Remote error: {err.Message}");
        }

        if (frame.Type != expectedType)
            throw new InvalidDataException($"Expected frame {expectedType}, got {frame.Type}.");

        return DeserializePayload<T>(frame.Payload);
    }
}
