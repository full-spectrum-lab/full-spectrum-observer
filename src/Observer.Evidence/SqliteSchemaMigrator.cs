using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FullSpectrum.Observer.Evidence.NativeSqlite;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Evidence;

internal static class SqliteSchemaMigrator
{
    private const string SchemaId = "FS-OBS-DB-001";

    public static void Apply(SqliteConnection connection, string appliedAtUtc)
    {
        connection.Execute("PRAGMA foreign_keys=ON;");
        connection.Execute("PRAGMA journal_mode=DELETE;");
        connection.Execute("PRAGMA synchronous=FULL;");

        string sql = LoadMigration();
        string checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        connection.BeginImmediate();
        try
        {
            connection.Execute(sql);
            using SqliteStatement query = connection.Prepare("SELECT checksum FROM schema_versions WHERE schema_id=?1;");
            query.BindText(1, SchemaId);
            if (query.Step() == SqliteStepResult.Row)
            {
                string existing = query.GetText(0);
                if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                {
                    throw new EvidenceStoreException(
                        FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED,
                        $"Migration checksum mismatch for {SchemaId}.");
                }
            }
            else
            {
                using SqliteStatement insert = connection.Prepare(
                    "INSERT INTO schema_versions(schema_id,applied_at_utc,checksum) VALUES(?1,?2,?3);");
                insert.BindText(1, SchemaId);
                insert.BindText(2, appliedAtUtc);
                insert.BindText(3, checksum);
                insert.ExecuteDone();
            }
            connection.Commit();
        }
        catch
        {
            connection.RollbackNoThrow();
            throw;
        }
    }

    private static string LoadMigration()
    {
        Assembly assembly = typeof(SqliteSchemaMigrator).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(
            static name => name.EndsWith(".Migrations.001_foundation.sql", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Embedded SQLite migration is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
