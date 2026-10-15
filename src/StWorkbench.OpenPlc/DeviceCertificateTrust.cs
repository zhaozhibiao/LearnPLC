using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace StWorkbench.OpenPlc;
public static class DeviceCertificateTrust
{
    public static bool Validate(X509Certificate2? certificate, SslPolicyErrors errors, string sha256Fingerprint)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || sha256Fingerprint is null) return false;
        var pin = sha256Fingerprint.Replace(":", "", StringComparison.Ordinal);
        if (pin.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c))) return false;
        return CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pin));
    }
}
