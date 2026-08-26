using System.Text.Json;
using System.Text.Json.Serialization;
using FullSpectrum.Observer.Contracts;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Resolves the identity asserted by a sealed internal-candidate package.
/// The override is intentionally available only through the package launcher,
/// which pins the file to the package root; normal source execution retains
/// the frozen public build identity.
/// </summary>
internal static class PackageIdentityResolver
{
    private const string PackageRootVariable = "FS_OBSERVER_PACKAGE_ROOT";
    private const string IdentityFileVariable = "FS_OBSERVER_PACKAGE_IDENTITY";
    private const string IdentityFileName = "internal-candidate-identity.json";
    private const string Contract = "fs-observer/package-identity/1";

    internal static bool TryResolve(out PackageIdentity? identity, out string? error)
    {
        identity = null;
        error = null;
        string? packageRoot = Environment.GetEnvironmentVariable(PackageRootVariable);
        string? identityFile = Environment.GetEnvironmentVariable(IdentityFileVariable);
        if (string.IsNullOrWhiteSpace(packageRoot) && string.IsNullOrWhiteSpace(identityFile))
            return true;
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(identityFile))
        {
            error = "Package identity override is incomplete.";
            return false;
        }

        try
        {
            string root = Path.GetFullPath(packageRoot);
            string expectedFile = Path.Combine(root, IdentityFileName);
            string actualFile = Path.GetFullPath(identityFile);
            if (!string.Equals(expectedFile, actualFile, StringComparison.OrdinalIgnoreCase))
            {
                error = "Package identity override must reference the package-root identity file.";
                return false;
            }
            if (!File.Exists(actualFile))
            {
                error = "Package identity file is missing.";
                return false;
            }

            PackageIdentity? parsed = JsonSerializer.Deserialize<PackageIdentity>(File.ReadAllText(actualFile));
            if (parsed is null ||
                !string.Equals(parsed.Contract, Contract, StringComparison.Ordinal) ||
                !string.Equals(parsed.SystemVersion, "v0.4.0-beta-internal-candidate", StringComparison.Ordinal) ||
                !string.Equals(parsed.ImplementationGate, "INTERNAL_CANDIDATE", StringComparison.Ordinal) ||
                !string.Equals(parsed.Maturity, "NO_GO_UNTIL_EXTERNAL_GATES", StringComparison.Ordinal) ||
                parsed.ReleaseAuthorized ||
                !string.Equals(parsed.EngineVersion, BuildIdentity.EngineVersion, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(parsed.SourceCommit))
            {
                error = "Package identity file violates the internal-candidate contract.";
                return false;
            }

            identity = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = "Package identity file could not be verified.";
            return false;
        }
    }
}

internal sealed record PackageIdentity(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("system_version")] string SystemVersion,
    [property: JsonPropertyName("implementation_gate")] string ImplementationGate,
    [property: JsonPropertyName("maturity")] string Maturity,
    [property: JsonPropertyName("release_authorized")] bool ReleaseAuthorized,
    [property: JsonPropertyName("source_commit")] string SourceCommit,
    [property: JsonPropertyName("engine_version")] string EngineVersion);
