using System.Security.Cryptography;
using System.Text;

namespace SwiftBeam.Core.Security;

public static class PinAuth
{
    public static string GeneratePin()
    {
        int value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    public static string FormatPin(string pin)
    {
        var normalized = NormalizePin(pin);
        return normalized.Length == 6 ? $"{normalized[..3]}-{normalized[3..]}" : normalized;
    }

    public static string NormalizePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return string.Empty;

        var sb = new StringBuilder(pin.Length);
        foreach (char c in pin)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static byte[] GenerateNonce(int size = 32)
    {
        byte[] nonce = new byte[size];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public static byte[] ComputeProof(string pin, byte[] serverNonce, byte[] clientNonce, byte[]? tlsCertHash = null)
    {
        string normalized = NormalizePin(pin);
        byte[] key = Encoding.UTF8.GetBytes(normalized);

        int payloadLen = serverNonce.Length + clientNonce.Length + (tlsCertHash?.Length ?? 0);
        byte[] payload = new byte[payloadLen];
        Buffer.BlockCopy(serverNonce, 0, payload, 0, serverNonce.Length);
        Buffer.BlockCopy(clientNonce, 0, payload, serverNonce.Length, clientNonce.Length);
        if (tlsCertHash is { Length: > 0 })
        {
            Buffer.BlockCopy(tlsCertHash, 0, payload, serverNonce.Length + clientNonce.Length, tlsCertHash.Length);
        }

        return HMACSHA256.HashData(key, payload);
    }

    public static bool VerifyProof(string expectedPin, byte[] serverNonce, byte[] clientNonce, byte[] providedProof, byte[]? tlsCertHash = null)
    {
        if (providedProof.Length != 32)
            return false;

        byte[] expected = ComputeProof(expectedPin, serverNonce, clientNonce, tlsCertHash);
        return CryptographicOperations.FixedTimeEquals(expected, providedProof);
    }
}
