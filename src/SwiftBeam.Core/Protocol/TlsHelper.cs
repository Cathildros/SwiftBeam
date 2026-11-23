using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SwiftBeam.Core.Protocol;

public static class TlsHelper
{
    public static X509Certificate2 CreateEphemeralCertificate(string deviceName)
    {
        string safeName = string.IsNullOrWhiteSpace(deviceName) ? "SwiftBeam" : deviceName.Replace("\"", "").Replace(",", "");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            $"CN=SwiftBeam-{safeName}",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                false));

        var now = DateTimeOffset.UtcNow;
        using var rawCert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));

        // Re-import via PKCS#12 so Windows Schannel has a usable private key handle.
        byte[] pfxBytes = rawCert.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch
        {
            return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.EphemeralKeySet);
        }
    }

    public static byte[] GetCertHash(X509Certificate cert)
    {
        return cert.GetCertHash(HashAlgorithmName.SHA256);
    }

    public static string FormatFingerprint(byte[] certHash)
    {
        string hex = Convert.ToHexString(certHash);
        // Short human-readable fingerprint (first 16 hex chars grouped by 4)
        return hex.Length >= 16
            ? $"{hex[..4]}:{hex[4..8]}:{hex[8..12]}:{hex[12..16]}"
            : hex;
    }

    public static SslServerAuthenticationOptions CreateServerOptions(X509Certificate2 serverCert)
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
    }

    public static SslClientAuthenticationOptions CreateClientOptions(
        string targetHost,
        Action<byte[]>? onRemoteCertHash = null)
    {
        return new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(targetHost) ? "swiftbeam.local" : targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                byte[] hash = GetCertHash(cert);
                onRemoteCertHash?.Invoke(hash);
                return true;
            }
        };
    }
}
