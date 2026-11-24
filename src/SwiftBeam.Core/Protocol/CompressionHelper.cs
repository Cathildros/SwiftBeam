using System.Buffers.Binary;
using System.IO.Compression;

namespace SwiftBeam.Core.Protocol;

/// <summary>
/// Fast Brotli (quality 1) wire compression that automatically skips already-compressed
/// media/archives and falls back to raw frames unless compression saves &gt;= 8% on the wire.
/// </summary>
public static class CompressionHelper
{
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".br",
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v",
        ".mp3", ".aac", ".ogg", ".opus", ".flac", ".m4a",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".pdf", ".docx", ".xlsx", ".pptx", ".apk", ".ipa", ".iso", ".nsp", ".xci", ".woff2"
    };

    public static bool ShouldCompressFile(string path)
    {
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || !IncompressibleExtensions.Contains(ext);
    }

    /// <summary>
    /// Pre-frames a Brotli-compressed packet in <paramref name="destWith9ByteHeader"/>:
    /// [0x2A][4-byte frame payload length = 4 + compressedLen][4-byte rawLen][compressedBytes].
    /// </summary>
    public static bool TryCompressPreframed(ReadOnlySpan<byte> rawData, byte[] destWith9ByteHeader, out int totalPacketBytes)
    {
        totalPacketBytes = 0;
        if (rawData.Length < 4096 || destWith9ByteHeader.Length < 9 + rawData.Length)
            return false;

        int maxCompressedOut = rawData.Length - 9;
        if (!BrotliEncoder.TryCompress(rawData, destWith9ByteHeader.AsSpan(9, maxCompressedOut), out int written, quality: 1, window: 22))
            return false;

        if (written <= 0 || written >= (int)(rawData.Length * 0.92))
            return false;

        destWith9ByteHeader[0] = (byte)FrameType.CompressedDataChunk;
        BinaryPrimitives.WriteInt32BigEndian(destWith9ByteHeader.AsSpan(1, 4), written + 4);
        BinaryPrimitives.WriteInt32BigEndian(destWith9ByteHeader.AsSpan(5, 4), rawData.Length);
        totalPacketBytes = 9 + written;
        return true;
    }

    public static int DecompressFramePayload(ReadOnlySpan<byte> framePayload, Span<byte> rawDestination)
    {
        if (framePayload.Length < 4)
            throw new InvalidDataException("Compressed data frame is too short.");

        int expectedRawLen = BinaryPrimitives.ReadInt32BigEndian(framePayload.Slice(0, 4));
        if (expectedRawLen < 0 || expectedRawLen > rawDestination.Length)
            throw new InvalidDataException($"Invalid compressed payload uncompressed length: {expectedRawLen}.");

        if (expectedRawLen == 0)
            return 0;

        if (!BrotliDecoder.TryDecompress(framePayload.Slice(4), rawDestination.Slice(0, expectedRawLen), out int bytesWritten) ||
            bytesWritten != expectedRawLen)
        {
            throw new InvalidDataException("Failed to decompress SwiftBeam data chunk.");
        }

        return expectedRawLen;
    }
}
