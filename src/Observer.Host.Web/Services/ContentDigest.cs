using System.Security.Cryptography;
using System.Text;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>Computes content digests (sha256, lowercase hex). Digests are always computed,
/// never fabricated (red line #8).</summary>
public static class ContentDigest
{
    public static string Compute(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
