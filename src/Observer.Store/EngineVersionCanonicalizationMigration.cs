using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Store;

/// <summary>
/// SD-001 / M3-FIX-05 — canonicalizes the Engine version stored in
/// <c>runtime_snapshots.engine_version</c> (and any other store column carrying the Engine
/// identity) from the legacy, wire-only form <c>'1.5.0'</c> to the frozen canonical
/// <c>'v1.5.0'</c> (equal to <see cref="EngineVersionContract.CanonicalVersion"/> /
/// <c>EngineV15Contract.EngineTag</c>).
///
/// <para>SQLite cannot ALTER a CHECK constraint, so the migration uses the safe rebuild flow:
/// <list type="bullet">
///   <item><description>Open a transaction.</description></item>
///   <item><description>Create <c>runtime_snapshots_new</c> with the corrected CHECK
///     (<c>engine_version = 'v1.5.0'</c>).</description></item>
///   <item><description>Copy every existing row, canonicalizing <c>engine_version</c> via
///     <see cref="EngineVersionContract.NormalizeLegacy"/> (legacy <c>'1.5.0'</c> →
///     <c>'v1.5.0'</c>; already-canonical rows are preserved unchanged). An unrecognized value makes
///     NormalizeLegacy throw, which rolls the whole migration back rather than silently dropping
///     data.</description></item>
///   <item><description>Verify the business row count is preserved before swapping.</description></item>
///   <item><description>DROP the old table and RENAME the new one into place; recreate indexes /
///     foreign keys.</description></item>
///   <item><description>Run <c>PRAGMA foreign_key_check</c>; any violation rolls back.</description></item>
///   <item><description>Record the migration in <c>schema_migrations</c> for idempotency.</description></item>
/// </list>
/// </para>
///
/// <para>Idempotent: a freshly-created database (CHECK already <c>'v1.5.0'</c>), or a database on
/// which the migration has already run, is a no-op. Transactional with full rollback on any failure
/// — no historical data is ever silently discarded. Applied both at application start (via
/// <see cref="ObserverStore.EnsureSchemaAsync"/>) and on demand
/// (<see cref="ObserverStore.ApplyEngineVersionCanonicalizationAsync"/>).</para>
/// </summary>
internal static class EngineVersionCanonicalizationMigration
{
    /// <summary>Migration identifier — must equal <see cref="EngineVersionContract.MigrationId"/>.</summary>
    public const string MigrationId = EngineVersionContract.MigrationId;

    private const string CanonicalCheckLiteral = "'v1.5.0'";

    public static async Task ApplyAsync(SqliteConnection connection)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            await EnsureMigrationTableAsync(connection, transaction);

            // Idempotency guard: a database on which this migration already ran is a no-op.
            if (await IsAlreadyAppliedAsync(connection, transaction))
            {
                await transaction.CommitAsync();
                return;
            }

            // grep across Init.sql confirmed runtime_snapshots.engine_version is the SOLE column
            // carrying the Engine version in this schema. If it is already canonical, skip the
            // rebuild entirely (a freshly-created database needs no data movement).
            bool alreadyCanonical = await IsRuntimeSnapshotsCanonicalAsync(connection, transaction);
            if (!alreadyCanonical)
            {
                await RebuildRuntimeSnapshotsAsync(connection, transaction);
            }

            await RecordAppliedAsync(connection, transaction);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task EnsureMigrationTableAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id TEXT PRIMARY KEY,
                applied_at_utc TEXT NOT NULL,
                checksum TEXT NOT NULL
            )";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsAlreadyAppliedAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id = @mid";
        command.Parameters.AddWithValue("@mid", MigrationId);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    private static async Task<bool> IsRuntimeSnapshotsCanonicalAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        string? ddl = await GetTableDdlAsync(connection, transaction, "runtime_snapshots");
        if (ddl is null)
        {
            // Table missing: Init.sql (run before this migration) creates it canonical, so nothing to do.
            return true;
        }
        // Canonical when the DDL already pins the 'v1.5.0' literal; the legacy form pins '1.5.0'.
        return ddl.Contains(CanonicalCheckLiteral, StringComparison.Ordinal);
    }

    private static async Task RebuildRuntimeSnapshotsAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = @"
                CREATE TABLE runtime_snapshots_new (
                  snapshot_id     TEXT PRIMARY KEY,
                  result_id       TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
                  analyzer_version TEXT NOT NULL,
                  engine_version   TEXT NOT NULL CHECK (engine_version = 'v1.5.0'),
                  profile_version  TEXT NOT NULL,
                  schema_version   TEXT NOT NULL,
                  input_digest     TEXT NOT NULL,
                  config_digest    TEXT NOT NULL,
                  runtime_digest   TEXT NOT NULL,
                  resolved_simulation_id TEXT NULL
                )";
            await create.ExecuteNonQueryAsync();
        }

        // Copy every row, canonicalizing engine_version via the explicit contract (legacy
        // '1.5.0' -> 'v1.5.0'; an unrecognized value throws and rolls the migration back).
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            bool hasResolvedSimulationId = await HasColumnAsync(
                connection, transaction, "runtime_snapshots", "resolved_simulation_id");
            read.CommandText = @"
                SELECT snapshot_id, result_id, analyzer_version, engine_version, profile_version,
                       schema_version, input_digest, config_digest, runtime_digest, " +
                (hasResolvedSimulationId ? "resolved_simulation_id" : "NULL AS resolved_simulation_id") +
                " FROM runtime_snapshots";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string canonicalEngineVersion = EngineVersionContract.NormalizeLegacy(reader.GetString(3));
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT INTO runtime_snapshots_new
                        (snapshot_id, result_id, analyzer_version, engine_version, profile_version, schema_version, input_digest, config_digest, runtime_digest, resolved_simulation_id)
                    VALUES (@id, @rid, @av, @ev, @pv, @sv, @idig, @cdig, @rdig, @rsid)";
                insert.Parameters.AddWithValue("@id", reader.GetString(0));
                insert.Parameters.AddWithValue("@rid", reader.GetString(1));
                insert.Parameters.AddWithValue("@av", reader.GetString(2));
                insert.Parameters.AddWithValue("@ev", canonicalEngineVersion);
                insert.Parameters.AddWithValue("@pv", reader.GetString(4));
                insert.Parameters.AddWithValue("@sv", reader.GetString(5));
                insert.Parameters.AddWithValue("@idig", reader.GetString(6));
                insert.Parameters.AddWithValue("@cdig", reader.GetString(7));
                insert.Parameters.AddWithValue("@rdig", reader.GetString(8));
                insert.Parameters.AddWithValue("@rsid", reader.IsDBNull(9) ? DBNull.Value : reader.GetString(9));
                try
                {
                    await insert.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex) when (
                    (int)ex.SqliteErrorCode == 787 ||
                    ex.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
                {
                    // FK enforcement is ON in ObserverStore connections, so an orphan row
                    // (result_id with no parent analysis_results) is rejected during the copy
                    // itself, before the PRAGMA foreign_key_check gate below. Surface it with the
                    // same canonical error code the PRAGMA gate would have raised, so callers get a
                    // single, deterministic failure reason and the whole migration rolls back.
                    throw new StoreException(
                        "STORE_MIGRATION_FK_VIOLATION",
                        "Foreign-key violation detected while rebuilding runtime_snapshots during " +
                        "engine-version canonicalization: " + ex.Message);
                }
            }
        }

        long oldCount = await CountScalarAsync(connection, transaction, "SELECT COUNT(*) FROM runtime_snapshots");
        long newCount = await CountScalarAsync(connection, transaction, "SELECT COUNT(*) FROM runtime_snapshots_new");
        if (oldCount != newCount)
        {
            throw new StoreException(
                "STORE_MIGRATION_ROWCOUNT_MISMATCH",
                $"Engine-version canonicalization would lose data: runtime_snapshots had {oldCount} rows but the rebuilt table has {newCount}.");
        }

        await using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE runtime_snapshots";
            await drop.ExecuteNonQueryAsync();
        }
        await using (var rename = connection.CreateCommand())
        {
            rename.Transaction = transaction;
            rename.CommandText = "ALTER TABLE runtime_snapshots_new RENAME TO runtime_snapshots";
            await rename.ExecuteNonQueryAsync();
        }

        // Recreate indexes (none on runtime_snapshots today) and verify FK integrity.
        await using (var fk = connection.CreateCommand())
        {
            fk.Transaction = transaction;
            fk.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await fk.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                throw new StoreException(
                    "STORE_MIGRATION_FK_VIOLATION",
                    "Foreign-key violation detected after engine-version canonicalization rebuild.");
            }
        }
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task RecordAppliedAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_migrations (migration_id, applied_at_utc, checksum) VALUES (@mid, @at, @cs)";
        command.Parameters.AddWithValue("@mid", MigrationId);
        command.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@cs", SchemaDefinition.Digest);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> GetTableDdlAsync(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@t";
        command.Parameters.AddWithValue("@t", tableName);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<long> CountScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
