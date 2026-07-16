using System.Security.Cryptography;
using System.Text;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Holds the one-time bootstrap token the Launcher minted and passed to the Host via
/// <c>--bootstrap-token</c> (ADR-005 L3). The Host validates it once to exchange for the HttpOnly
/// session cookie. This context is the seam; the token is NEVER written to logs, diagnostics, or
/// the database (L9/L16), and is compared in constant time to avoid timing leaks.
/// </summary>
public sealed class BootstrapTokenContext
{
    public string? Token { get; }
    public DateTimeOffset IssuedAt { get; }
    public TimeSpan Lifetime { get; }

    /// <summary>Set true once a request presents a valid token. The actual HttpOnly session
    /// cookie exchange (L4/L12) is implemented in a subsequent module; this flag is the seam.</summary>
    public bool HandshakeVerified { get; set; }

    public BootstrapTokenContext(string? token, TimeSpan lifetime)
    {
        Token = token;
        IssuedAt = DateTimeOffset.UtcNow;
        Lifetime = lifetime;
    }

    public bool IsExpired(DateTimeOffset now) => now - IssuedAt > Lifetime;

    public static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        ReadOnlySpan<byte> left = Encoding.UTF8.GetBytes(a);
        ReadOnlySpan<byte> right = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
