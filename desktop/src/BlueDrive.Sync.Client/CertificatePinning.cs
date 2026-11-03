using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueDrive.Sync.Client;

/// <summary>
/// Optional certificate pinning for an internal BlueDrive server (spec 9, deployment setting): besides the normal TLS validation, one
/// certificate of the server's chain must have a public key (SPKI) whose SHA-256, base64, is in <see cref="Pins"/>. Empty = normal TLS only.
/// Get a pin: <c>openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64</c>.
/// </summary>
public static class CertificatePinning
{
    private static volatile string[] _pins = [];

    public static IReadOnlyList<string> Pins
    {
        get => _pins;
        set => _pins = value.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }

    public static string SpkiSha256(X509Certificate2 cert) => Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>TLS callback: normal validation, then the pin (any certificate of the chain, so a pinned CA or intermediate works too).</summary>
    public static bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None || certificate is null) return false;
        var pins = _pins;
        if (pins.Length == 0) return true;
        using var leaf = new X509Certificate2(certificate);
        if (pins.Contains(SpkiSha256(leaf))) return true;
        return chain?.ChainElements.Any(e => pins.Contains(SpkiSha256(e.Certificate))) ?? false;
    }

    public static readonly RemoteCertificateValidationCallback Callback = (_, cert, chain, errors) => Validate(cert, chain, errors);

    /// <summary>The handler every BlueDrive connection uses (API, sign-in, SignalR long polling).</summary>
    public static SocketsHttpHandler Handler(bool allowRedirect = false) => new()
    {
        AllowAutoRedirect = allowRedirect,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Callback },
    };
}
