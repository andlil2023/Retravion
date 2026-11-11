using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Retravion.Web.Features.Mcp;

internal static class TerminalCertificate
{
    // Public fingerprint of the configured HTTPS certificate, for comparison during terminal setup.
    // Null when the host does not configure one here (for example a development certificate).
    internal static string? Fingerprint(IConfiguration configuration)
    {
        var path = configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration["Kestrel:Certificates:Default:Password"]);
            return string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(pair => new string(pair)));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
