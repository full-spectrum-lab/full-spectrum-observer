using System.Security.Cryptography;

namespace FullSpectrum.Observer.Store;

/// <summary>
/// Observer-side schema definition metadata. The <see cref="Digest"/> is computed (not
/// fabricated) from the canonical <c>Init.sql</c> embedded resource, per R1-B §10.1
/// (schema_digest = sha256 of the Schema definition file the Observer controls).
/// </summary>
public static class SchemaDefinition
{
    /// <summary>Observer schema version this console is authored against.</summary>
    public const string Version = "fs-obs-console-schema/1.0.0";

    private static string? _cachedDigest;

    /// <summary>Computes the sha256 (lowercase hex) of the canonical <c>Init.sql</c> schema.</summary>
    public static string Digest
    {
        get
        {
            if (_cachedDigest is null)
            {
                var assembly = typeof(SchemaDefinition).Assembly;
                const string resourceName = "FullSpectrum.Observer.Store.Data.Migrations.Init.sql";
                using var stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new StoreException("STORE_MIGRATION_MISSING", $"Embedded resource {resourceName} was not found.");
                byte[] hash = SHA256.HashData(stream);
                _cachedDigest = Convert.ToHexStringLower(hash);
            }
            return _cachedDigest;
        }
    }
}
