using System.Text;

namespace SwiftBeam.Core.Protocol;

/// <summary>
/// Pure C# ISO/IEC 18004 QR Code encoder (Byte mode, Error Correction Level M, Versions 1..10).
/// Generates crisp black/white module matrices without external dependencies.
/// </summary>
public static class QrCodeEncoder
{
    private readonly record struct VersionInfo(int Version, int DataCodewords, int EccPerBlock, int BlocksGroup1, int DataPerBlock1, int BlocksGroup2, int DataPerBlock2, int[] AlignmentCenters);

    private static readonly VersionInfo[] Versions =
    [
        new(1, 16, 10, 1, 16, 0, 0, []),
        new(2, 28, 16, 1, 28, 0, 0, [6, 18]),
        new(3, 44, 26, 1, 44, 0, 0, [6, 22]),
        new(4, 64, 18, 2, 32, 0, 0, [6, 26]),
        new(5, 86, 24, 2, 43, 0, 0, [6, 30]),
        new(6, 108, 16, 4, 27, 0, 0, [6, 34]),
        new(7, 124, 18, 4, 31, 0, 0, [6, 22, 38]),
        new(8, 154, 22, 2, 38, 2, 39, [6, 24, 42]),
        new(9, 182, 22, 3, 36, 2, 37, [6, 26, 46]),
        new(10, 216, 26, 4, 43, 1, 44, [6, 28, 50])
    ];

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCodeEncoder()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++)
            GfExp[i] = GfExp[i - 255];
    }

    public static bool[,] Encode(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);

        // Byte mode header: 4-bit mode (0100) + char count bits (8 for v1..9, 16 for v10+)
        VersionInfo? chosen = null;
        foreach (var v in Versions)
        {
            int countBits = v.Version <= 9 ? 8 : 16;
            int neededBits = 4 + countBits + utf8.Length * 8;
            if (neededBits <= v.DataCodewords * 8)
            {
                chosen = v;
                break;
            }
        }

        if (chosen is null)
            throw new ArgumentException($"QR input too long ({utf8.Length} bytes; max ~213 bytes).");
        var ver = chosen.Value;

        var bits = new List<bool>(ver.DataCodewords * 8);
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, utf8.Length, ver.Version <= 9 ? 8 : 16);
        foreach (byte b in utf8)
            AppendBits(bits, b, 8);

        int capacityBits = ver.DataCodewords * 8;
        int terminator = Math.Min(4, capacityBits - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0)
            bits.Add(false);

        byte[] dataCodewords = new byte[ver.DataCodewords];
        for (int i = 0; i < bits.Count / 8; i++)
        {
            byte val = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[i * 8 + b])
                    val |= (byte)(1 << (7 - b));
            }
            dataCodewords[i] = val;
        }

        int padIdx = bits.Count / 8;
        for (int i = padIdx; i < dataCodewords.Length; i++)
            dataCodewords[i] = (byte)((i - padIdx) % 2 == 0 ? 0xEC : 0x11);

        // Split into blocks + compute Reed-Solomon ECC per block, then interleave
        var dataBlocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        int offset = 0;
        byte[] genPoly = ComputeGeneratorPolynomial(ver.EccPerBlock);

        for (int i = 0; i < ver.BlocksGroup1; i++)
        {
            byte[] blk = dataCodewords.AsSpan(offset, ver.DataPerBlock1).ToArray();
            offset += ver.DataPerBlock1;
            dataBlocks.Add(blk);
            eccBlocks.Add(ComputeEcc(blk, genPoly));
        }
        for (int i = 0; i < ver.BlocksGroup2; i++)
        {
            byte[] blk = dataCodewords.AsSpan(offset, ver.DataPerBlock2).ToArray();
            offset += ver.DataPerBlock2;
            dataBlocks.Add(blk);
            eccBlocks.Add(ComputeEcc(blk, genPoly));
        }

        var interleaved = new List<byte>(ver.DataCodewords + eccBlocks.Count * ver.EccPerBlock);
        int maxDataLen = Math.Max(ver.DataPerBlock1, ver.DataPerBlock2);
        for (int col = 0; col < maxDataLen; col++)
        {
            foreach (var blk in dataBlocks)
            {
                if (col < blk.Length)
                    interleaved.Add(blk[col]);
            }
        }
        for (int col = 0; col < ver.EccPerBlock; col++)
        {
            foreach (var ecc in eccBlocks)
                interleaved.Add(ecc[col]);
        }

        int size = 17 + 4 * ver.Version;
        bool[,] modules = new bool[size, size];
        bool[,] isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, size, ver);
        PlaceCodewords(modules, isFunction, size, interleaved);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, size, mask);
            DrawFormatBits(modules, isFunction, size, mask);
            int penalty = EvaluatePenalty(modules, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
            ApplyMask(modules, isFunction, size, mask);
        }

        ApplyMask(modules, isFunction, size, bestMask);
        DrawFormatBits(modules, isFunction, size, bestMask);
        return modules;
    }

    private static void AppendBits(List<bool> dest, int value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
            dest.Add(((value >> i) & 1) != 0);
    }

    private static byte GfMultiply(byte a, byte b) =>
        (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    private static byte[] ComputeGeneratorPolynomial(int degree)
    {
        byte[] poly = [1];
        for (int i = 0; i < degree; i++)
        {
            byte[] next = new byte[poly.Length + 1];
            byte root = GfExp[i];
            for (int j = 0; j < poly.Length; j++)
            {
                next[j] ^= poly[j];
                next[j + 1] ^= GfMultiply(poly[j], root);
            }
            poly = next;
        }
        return poly;
    }

    private static byte[] ComputeEcc(byte[] data, byte[] genPoly)
    {
        int eccLen = genPoly.Length - 1;
        byte[] rem = new byte[eccLen];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, eccLen - 1);
            rem[eccLen - 1] = 0;
            for (int j = 0; j < eccLen; j++)
                rem[j] ^= GfMultiply(genPoly[j + 1], factor);
        }
        return rem;
    }

    private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int size, int startX, int startY)
    {
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int x = startX + dx;
                int y = startY + dy;
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                int dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                bool dark = dist != 2 && dist != 4;
                SetFunctionModule(modules, isFunction, x, y, dark);
            }
        }
    }

    private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunctionModule(modules, isFunction, cx + dx, cy + dy, dist != 1);
            }
        }
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int size, VersionInfo ver)
    {
        // Timing patterns
        for (int i = 0; i < size; i++)
        {
            bool dark = (i % 2) == 0;
            SetFunctionModule(modules, isFunction, 6, i, dark);
            SetFunctionModule(modules, isFunction, i, 6, dark);
        }

        // Three finder patterns
        DrawFinderPattern(modules, isFunction, size, 0, 0);
        DrawFinderPattern(modules, isFunction, size, size - 7, 0);
        DrawFinderPattern(modules, isFunction, size, 0, size - 7);

        // Alignment patterns
        int[] centers = ver.AlignmentCenters;
        for (int i = 0; i < centers.Length; i++)
        {
            for (int j = 0; j < centers.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == centers.Length - 1) || (i == centers.Length - 1 && j == 0))
                    continue;
                DrawAlignmentPattern(modules, isFunction, centers[j], centers[i]);
            }
        }

        // Reserve format info + dark module
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                SetFunctionModule(modules, isFunction, 8, i, false);
                SetFunctionModule(modules, isFunction, i, 8, false);
            }
        }
        for (int i = size - 8; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, i, 8, false);
            SetFunctionModule(modules, isFunction, 8, i, false);
        }
        SetFunctionModule(modules, isFunction, 8, size - 8, true);

        // Version info reserved blocks for v7..10
        if (ver.Version >= 7)
        {
            int rem = ver.Version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ (((rem >> 11) & 1) * 0x1F25);
            int bits = (ver.Version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + (i % 3);
                int b = i / 3;
                SetFunctionModule(modules, isFunction, a, b, bit);
                SetFunctionModule(modules, isFunction, b, a, bit);
            }
        }
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int size, int mask)
    {
        // Level M = 0b00
        int data = (0b00 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++)
            SetFunctionModule(modules, isFunction, 8, i, ((bits >> i) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 7, ((bits >> 6) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 8, ((bits >> 7) & 1) != 0);
        SetFunctionModule(modules, isFunction, 7, 8, ((bits >> 8) & 1) != 0);
        for (int i = 9; i < 15; i++)
            SetFunctionModule(modules, isFunction, 14 - i, 8, ((bits >> i) & 1) != 0);

        for (int i = 0; i < 8; i++)
            SetFunctionModule(modules, isFunction, size - 1 - i, 8, ((bits >> i) & 1) != 0);
        for (int i = 8; i < 15; i++)
            SetFunctionModule(modules, isFunction, 8, size - 15 + i, ((bits >> i) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, size - 8, true);
    }

    private static void PlaceCodewords(bool[,] modules, bool[,] isFunction, int size, List<byte> codewords)
    {
        int bitIdx = 0;
        int totalBits = codewords.Count * 8;
        int x = size - 1;
        bool upward = true;

        while (x >= 0)
        {
            if (x == 6) x--;
            for (int step = 0; step < size; step++)
            {
                int y = upward ? size - 1 - step : step;
                for (int dx = 0; dx <= 1; dx++)
                {
                    int col = x - dx;
                    if (col >= 0 && !isFunction[y, col])
                    {
                        bool dark = bitIdx < totalBits && ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        modules[y, col] = dark;
                        bitIdx++;
                    }
                }
            }
            x -= 2;
            upward = !upward;
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int size, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (isFunction[y, x]) continue;
                bool invert = mask switch
                {
                    0 => ((x + y) & 1) == 0,
                    1 => (y & 1) == 0,
                    2 => (x % 3) == 0,
                    3 => ((x + y) % 3) == 0,
                    4 => (((y / 2) + (x / 3)) & 1) == 0,
                    5 => ((x * y) % 2 + (x * y) % 3) == 0,
                    6 => ((((x * y) % 2) + ((x * y) % 3)) & 1) == 0,
                    7 => ((((x + y) % 2) + ((x * y) % 3)) & 1) == 0,
                    _ => false
                };
                if (invert)
                    modules[y, x] = !modules[y, x];
            }
        }
    }

    private static int EvaluatePenalty(bool[,] modules, int size)
    {
        int penalty = 0;

        // Horizontal and vertical runs >= 5
        for (int y = 0; y < size; y++)
        {
            int runRow = 1, runCol = 1;
            for (int x = 1; x < size; x++)
            {
                if (modules[y, x] == modules[y, x - 1]) runRow++;
                else { if (runRow >= 5) penalty += 3 + (runRow - 5); runRow = 1; }

                if (modules[x, y] == modules[x - 1, y]) runCol++;
                else { if (runCol >= 5) penalty += 3 + (runCol - 5); runCol = 1; }
            }
            if (runRow >= 5) penalty += 3 + (runRow - 5);
            if (runCol >= 5) penalty += 3 + (runCol - 5);
        }

        // 2x2 blocks
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                bool c = modules[y, x];
                if (modules[y, x + 1] == c && modules[y + 1, x] == c && modules[y + 1, x + 1] == c)
                    penalty += 3;
            }
        }

        // Finder-like 1:1:3:1:1 patterns (rows & cols)
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x <= size - 11; x++)
            {
                if (MatchesFinderLikeRow(modules, y, x)) penalty += 40;
                if (MatchesFinderLikeCol(modules, y, x)) penalty += 40;
            }
        }

        int darkCount = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (modules[y, x]) darkCount++;

        int pct = darkCount * 100 / (size * size);
        penalty += Math.Abs(pct - 50) / 5 * 10;
        return penalty;
    }

    private static bool MatchesFinderLikeRow(bool[,] m, int r, int c) =>
        (m[r, c] && !m[r, c + 1] && m[r, c + 2] && m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] && !m[r, c + 7] && !m[r, c + 8] && !m[r, c + 9] && !m[r, c + 10]) ||
        (!m[r, c] && !m[r, c + 1] && !m[r, c + 2] && !m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] && m[r, c + 7] && m[r, c + 8] && !m[r, c + 9] && m[r, c + 10]);

    private static bool MatchesFinderLikeCol(bool[,] m, int c, int r) =>
        (m[r, c] && !m[r + 1, c] && m[r + 2, c] && m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] && !m[r + 7, c] && !m[r + 8, c] && !m[r + 9, c] && !m[r + 10, c]) ||
        (!m[r, c] && !m[r + 1, c] && !m[r + 2, c] && !m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] && m[r + 7, c] && m[r + 8, c] && !m[r + 9, c] && m[r + 10, c]);
}
