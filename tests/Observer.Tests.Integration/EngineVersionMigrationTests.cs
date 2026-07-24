using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M3-FIX-05 / SD-001 — Migration coverage for the Engine-version canonicalization
/// (MIG-OBS-V03-ENGINE-VERSION-CANONICALIZATION).
///
/// These tests exercise the REAL <see cref="ObserverStore"/> and the REAL
/// <see cref="EngineVersionCanonicalizationMigration"/> against temp SQLite files, with no Python
/// Engine required. They prove:
/// <list type="bullet">
///   <item><description>A freshly-created database (CHECK already 'v1.5.0') accepts the canonical
///     version and rejects illegal ones (DB CHECK defence-in-depth).</description></item>
///   <item><description>A pre-fix database (CHECK '1.5.0') is successfully migrated; legacy
///     '1.5.0' rows become 'v1.5.0'; already-canonical rows are unchanged.</description></item>
///   <item><description>The migration is idempotent (re-run is a no-op) and transactional: a
///     failure (foreign-key violation) rolls back and preserves the original data.</description></item>
///   <item><description>Business row counts are preserved and PRAGMA foreign_key_check passes after
///     the rebuild.</description></item>
/// </list>
/// </summary>
public sealed class EngineVersionMigrationTests
{
    private static async Task<(string DbPath, ObserverStore Store)> OpenFreshStoreAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-mig-fresh-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync(); // new Init.sql -> CHECK 'v1.5.0' + runs the migration (already canonical)
        return (dbPath, store);
    }

    /// <summary>Builds a PRE-FIX database directly: runtime_snapshots CHECK pins the legacy
    /// '1.5.0' form. Returns the store (migration NOT yet run) and the seeded result/snapshot ids.</summary>
    private static async Task<(string DbPath, ObserverStore Store, string ResultId, string SnapshotId)> OpenOldStoreAsync(
        string engineVersion, int snapshotCount = 1, bool orphan = false)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-mig-old-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);

        await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;"))
        {
            await connection.OpenAsync();
            // Test-setup connection: FK enforcement is intentionally OFF here so we can stage an
            // orphan runtime_snapshots row (result_id with no parent analysis_results) that the
            // real migration must later detect. The production ObserverStore path keeps FK ON and
            // relies on the migration's PRAGMA foreign_key_check gate.
            await using (var fkOff = connection.CreateCommand())
            {
                fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
                await fkOff.ExecuteNonQueryAsync();
            }
            await using var ddl = connection.CreateCommand();
            ddl.CommandText = OldSchemaSql;
            await ddl.ExecuteNonQueryAsync();

            // Seed a valid FK chain so PRAGMA foreign_key_check passes after the rebuild.
            await using var seed = connection.CreateCommand();
            if (!orphan)
            {
                seed.CommandText = @"
                    INSERT INTO subjects VALUES ('S-OLD','PERSON','OBSERVE',NULL,'2026-07-12T00:00:00Z');
                    INSERT INTO subject_versions VALUES ('SV-OLD','S-OLD','Active',1,'{}','1.0.0','2026-07-12T00:00:00Z',NULL,NULL);
                    INSERT INTO analysis_tasks VALUES ('T-OLD','SV-OLD','[]','FORM','{}','digest-old','[]','SANITIZED_PERSISTENT','Draft','NOT_REQUIRED','2026-07-12T00:00:00Z');
                    INSERT INTO analysis_results VALUES ('RES-OLD','T-OLD','{}','UNKNOWN',0,'2026-07-12T00:00:00Z');";
                await seed.ExecuteNonQueryAsync();
            }

            for (int i = 0; i < snapshotCount; i++)
            {
                string resultId = orphan ? "RES-ORPHAN" : "RES-OLD";
                string snapshotId = $"SNP-OLD-{i}";
                await using var snap = connection.CreateCommand();
                snap.CommandText = @"
                    INSERT INTO runtime_snapshots (snapshot_id, result_id, analyzer_version, engine_version, profile_version, schema_version, input_digest, config_digest, runtime_digest)
                    VALUES (@id, @rid, '1.5.0', @ev, '1.5.0', '1.0.0', 'dig', 'dig', 'dig')";
                snap.Parameters.AddWithValue("@id", snapshotId);
                snap.Parameters.AddWithValue("@rid", resultId);
                snap.Parameters.AddWithValue("@ev", engineVersion);
                await snap.ExecuteNonQueryAsync();
            }
        }
        SqliteConnection.ClearAllPools();

        var store = new ObserverStore(dbPath);
        return (dbPath, store, orphan ? "RES-ORPHAN" : "RES-OLD", $"SNP-OLD-0");
    }

    private static async Task CleanupAsync(ObserverStore store, string dbPath)
    {
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    private static long CountSnapshots(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runtime_snapshots";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string GetSnapshotEngineVersion(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT engine_version FROM runtime_snapshots WHERE snapshot_id = @id";
        command.Parameters.AddWithValue("@id", snapshotId);
        object? value = command.ExecuteScalar();
        return value is string s ? s : string.Empty;
    }

    private static bool SchemaMigrationsHas(string dbPath, string migrationId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;");
        connection.Open();
        // Guard: after a rolled-back migration the schema_migrations table itself is gone
        // (SQLite rolls back transactional DDL). Treat its absence as "not applied".
        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if (Convert.ToInt64(tableCheck.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id = @mid";
        command.Parameters.AddWithValue("@mid", migrationId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static int ForeignKeyCheckRowCount(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read()) rows++;
        return rows;
    }

    private const string OldSchemaSql = @"
        CREATE TABLE subjects (local_subject_id TEXT PRIMARY KEY, subject_type TEXT NOT NULL, mode TEXT NOT NULL, concentration_tier TEXT, created_at TEXT NOT NULL);
        CREATE TABLE subject_versions (version_id TEXT PRIMARY KEY, subject_id TEXT NOT NULL REFERENCES subjects(local_subject_id) ON DELETE RESTRICT, status TEXT NOT NULL, seq INTEGER NOT NULL, payload TEXT NOT NULL, schema_version TEXT NOT NULL, created_at TEXT NOT NULL, active_from TEXT, retired_at TEXT);
        CREATE TABLE analysis_tasks (task_id TEXT PRIMARY KEY, subject_version_id TEXT NOT NULL REFERENCES subject_versions(version_id) ON DELETE RESTRICT, knowledge_version_ids TEXT NOT NULL, input_mode TEXT NOT NULL, canonical_input TEXT NOT NULL, content_digest TEXT NOT NULL, transform_trace TEXT, retention_mode TEXT NOT NULL, status TEXT NOT NULL, review_status TEXT NOT NULL DEFAULT 'NOT_REQUIRED', created_at TEXT NOT NULL);
        CREATE TABLE analysis_results (result_id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES analysis_tasks(task_id) ON DELETE RESTRICT, conclusion_payload TEXT NOT NULL, unknown_state TEXT NOT NULL, hard_gate INTEGER NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE runtime_snapshots (
          snapshot_id     TEXT PRIMARY KEY,
          result_id       TEXT NOT NULL REFERENCES analysis_results(result_id) ON DELETE RESTRICT,
          analyzer_version TEXT NOT NULL,
          engine_version   TEXT NOT NULL CHECK (engine_version = '1.5.0'),
          profile_version  TEXT NOT NULL,
          schema_version   TEXT NOT NULL,
          input_digest     TEXT NOT NULL,
          config_digest    TEXT NOT NULL,
          runtime_digest   TEXT NOT NULL
        );";

    // ---- 1. New database accepts the canonical "v1.5.0" --------------------

    [Fact]
    public async Task Fresh_database_accepts_canonical_engine_version()
    {
        (string dbPath, ObserverStore store) = await OpenFreshStoreAsync();
        try
        {
            string taskId = "TASK-FRESH-001";
            await SeedSubjectForSnapshotAsync(store, taskId);
            var snapshot = new RuntimeSnapshot
            {
                SnapshotId = "SNP-FRESH-001",
                ResultId = "RES-TASK-FRESH-001",
                AnalyzerVersion = "1.5.0",
                EngineVersion = EngineVersionContract.CanonicalVersion, // "v1.5.0"
                ProfileVersion = "1.5.0",
                SchemaVersion = "1.0.0",
                InputDigest = "dig",
                ConfigDigest = "dig",
                RuntimeDigest = "dig",
            };
            Func<Task> act = async () => await store.InsertRuntimeSnapshotAsync(snapshot);
            await act.Should().NotThrowAsync();

            RuntimeSnapshot? persisted = await store.GetRuntimeSnapshotByResultAsync("RES-TASK-FRESH-001");
            persisted.Should().NotBeNull();
            persisted!.EngineVersion.Should().Be(EngineVersionContract.CanonicalVersion);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 2. New database rejects an illegal Engine version (DB CHECK) -----

    [Fact]
    public async Task Fresh_database_rejects_illegal_engine_version()
    {
        (string dbPath, ObserverStore store) = await OpenFreshStoreAsync();
        try
        {
            string taskId = "TASK-FRESH-002";
            await SeedSubjectForSnapshotAsync(store, taskId);
            var bad = new RuntimeSnapshot
            {
                SnapshotId = "SNP-FRESH-002",
                ResultId = "RES-TASK-FRESH-002",
                AnalyzerVersion = "1.5.0",
                EngineVersion = "9.9.9", // illegal
                ProfileVersion = "1.5.0",
                SchemaVersion = "1.0.0",
                InputDigest = "dig",
                ConfigDigest = "dig",
                RuntimeDigest = "dig",
            };
            Func<Task> act = async () => await store.InsertRuntimeSnapshotAsync(bad);
            await act.Should().ThrowAsync<Exception>().WithMessage("*CHECK*");
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 3. Old database (CHECK '1.5.0') migrates successfully -----------

    [Fact]
    public async Task Old_database_with_legacy_check_migrates_successfully()
    {
        (string dbPath, ObserverStore store, _, _) = await OpenOldStoreAsync("1.5.0");
        try
        {
            Func<Task> act = async () => await store.ApplyEngineVersionCanonicalizationAsync();
            await act.Should().NotThrowAsync();
            SchemaMigrationsHas(dbPath, EngineVersionContract.MigrationId).Should().BeTrue();
            // The CHECK is now canonical: a direct read of the stored literal confirms 'v1.5.0'.
            GetSnapshotEngineVersion(dbPath, "SNP-OLD-0").Should().Be("v1.5.0");
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 4. Old row '1.5.0' canonicalized to 'v1.5.0' ---------------------

    [Fact]
    public async Task Old_row_with_legacy_engine_version_canonicalized_to_v1_5_0()
    {
        (string dbPath, ObserverStore store, string resultId, _) = await OpenOldStoreAsync("1.5.0");
        try
        {
            await store.ApplyEngineVersionCanonicalizationAsync();
            RuntimeSnapshot? snapshot = await store.GetRuntimeSnapshotByResultAsync(resultId);
            snapshot.Should().NotBeNull();
            snapshot!.EngineVersion.Should().Be("v1.5.0");
            snapshot.EngineVersion.Should().Be(EngineVersionContract.CanonicalVersion);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 5. Already-canonical data is unchanged after migration ----------

    [Fact]
    public async Task Already_canonical_row_is_unchanged_after_migration()
    {
        (string dbPath, ObserverStore store) = await OpenFreshStoreAsync();
        try
        {
            await SeedSubjectForSnapshotAsync(store, "TASK-CANON");
            var snapshot = new RuntimeSnapshot
            {
                SnapshotId = "SNP-CANON",
                ResultId = "RES-TASK-CANON",
                AnalyzerVersion = "1.5.0",
                EngineVersion = EngineVersionContract.CanonicalVersion, // already 'v1.5.0'
                ProfileVersion = "1.5.0",
                SchemaVersion = "1.0.0",
                InputDigest = "dig",
                ConfigDigest = "dig",
                RuntimeDigest = "dig",
            };
            await store.InsertRuntimeSnapshotAsync(snapshot);
            string before = GetSnapshotEngineVersion(dbPath, "SNP-CANON");

            // A fresh DB is already canonical; the migration must be a no-op for the data.
            await store.ApplyEngineVersionCanonicalizationAsync();
            string after = GetSnapshotEngineVersion(dbPath, "SNP-CANON");

            before.Should().Be("v1.5.0");
            after.Should().Be("v1.5.0");
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 6. Migration is idempotent on repeat ----------------------------

    [Fact]
    public async Task Migration_is_idempotent_on_repeat()
    {
        (string dbPath, ObserverStore store, string resultId, _) = await OpenOldStoreAsync("1.5.0");
        try
        {
            await store.ApplyEngineVersionCanonicalizationAsync();
            string afterFirst = GetSnapshotEngineVersion(dbPath, "SNP-OLD-0");
            long countAfterFirst = CountSnapshots(dbPath);
            int fkAfterFirst = ForeignKeyCheckRowCount(dbPath);

            // Re-run: must be a no-op (already recorded) and produce identical results.
            await store.ApplyEngineVersionCanonicalizationAsync();
            string afterSecond = GetSnapshotEngineVersion(dbPath, "SNP-OLD-0");
            long countAfterSecond = CountSnapshots(dbPath);

            afterFirst.Should().Be("v1.5.0");
            afterSecond.Should().Be("v1.5.0");
            countAfterFirst.Should().Be(1);
            countAfterSecond.Should().Be(countAfterFirst);
            fkAfterFirst.Should().Be(0); // foreign_key_check passed
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 7. Migration failure rolls back and preserves data --------------

    [Fact]
    public async Task Migration_failure_rolls_back_and_preserves_original_data()
    {
        // Orphan runtime_snapshots row (engine_version '1.5.0') with no parent analysis_results:
        // the rebuild copies it, then PRAGMA foreign_key_check fails -> the whole migration rolls back.
        (string dbPath, ObserverStore store, _, _) = await OpenOldStoreAsync("1.5.0", orphan: true);
        try
        {
            Func<Task> act = async () => await store.ApplyEngineVersionCanonicalizationAsync();
            StoreException caught = (await act.Should().ThrowAsync<StoreException>()).Which;
            caught.ReasonCode.Should().Be("STORE_MIGRATION_FK_VIOLATION");

            // Rollback preserved the original (old-check) table and its row.
            CountSnapshots(dbPath).Should().Be(1);
            GetSnapshotEngineVersion(dbPath, "SNP-OLD-0").Should().Be("1.5.0");
            SchemaMigrationsHas(dbPath, EngineVersionContract.MigrationId).Should().BeFalse();

            // Recovery: insert the missing parent, then the migration succeeds.
            await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;"))
            {
                await connection.OpenAsync();
                await using var fix = connection.CreateCommand();
                fix.CommandText = @"
                    INSERT INTO subjects VALUES ('S-OLD','PERSON','OBSERVE',NULL,'2026-07-12T00:00:00Z');
                    INSERT INTO subject_versions VALUES ('SV-OLD','S-OLD','Active',1,'{}','1.0.0','2026-07-12T00:00:00Z',NULL,NULL);
                    INSERT INTO analysis_tasks VALUES ('T-OLD','SV-OLD','[]','FORM','{}','digest-old','[]','SANITIZED_PERSISTENT','Draft','NOT_REQUIRED','2026-07-12T00:00:00Z');
                    INSERT INTO analysis_results VALUES ('RES-ORPHAN','T-OLD','{}','UNKNOWN',0,'2026-07-12T00:00:00Z');";
                await fix.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            await store.ApplyEngineVersionCanonicalizationAsync();
            GetSnapshotEngineVersion(dbPath, "SNP-OLD-0").Should().Be("v1.5.0");
            ForeignKeyCheckRowCount(dbPath).Should().Be(0);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 8. foreign_key_check passes after a successful migration --------

    [Fact]
    public async Task Migration_passes_foreign_key_check()
    {
        (string dbPath, ObserverStore store, _, _) = await OpenOldStoreAsync("1.5.0");
        try
        {
            await store.ApplyEngineVersionCanonicalizationAsync();
            ForeignKeyCheckRowCount(dbPath).Should().Be(0);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 9. Business row count is preserved ------------------------------

    [Fact]
    public async Task Migration_preserves_business_row_count()
    {
        (string dbPath, ObserverStore store, _, _) = await OpenOldStoreAsync("1.5.0", snapshotCount: 5);
        try
        {
            long before = CountSnapshots(dbPath);
            before.Should().Be(5);
            await store.ApplyEngineVersionCanonicalizationAsync();
            long after = CountSnapshots(dbPath);
            after.Should().Be(before);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- Bonus: after migration, direct insert of legacy '1.5.0' is rejected (CHECK is canonical).

    [Fact]
    public async Task After_migration_legacy_1_5_0_is_rejected_by_canonical_check()
    {
        (string dbPath, ObserverStore store, _, _) = await OpenOldStoreAsync("1.5.0");
        try
        {
            await store.ApplyEngineVersionCanonicalizationAsync();

            await SeedSubjectForSnapshotAsync(store, "TASK-LEGACY");
            var legacy = new RuntimeSnapshot
            {
                SnapshotId = "SNP-LEGACY",
                ResultId = "RES-TASK-LEGACY",
                AnalyzerVersion = "1.5.0",
                EngineVersion = "1.5.0", // legacy form — must now be rejected by the canonical CHECK
                ProfileVersion = "1.5.0",
                SchemaVersion = "1.0.0",
                InputDigest = "dig",
                ConfigDigest = "dig",
                RuntimeDigest = "dig",
            };
            Func<Task> act = async () => await store.InsertRuntimeSnapshotAsync(legacy);
            await act.Should().ThrowAsync<Exception>().WithMessage("*CHECK*");

            // The canonical form still inserts fine.
            var canonical = legacy with { SnapshotId = "SNP-CANON2", ResultId = "RES-TASK-LEGACY", EngineVersion = EngineVersionContract.CanonicalVersion };
            Func<Task> ok = async () => await store.InsertRuntimeSnapshotAsync(canonical);
            await ok.Should().NotThrowAsync();
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    private static async Task SeedSubjectForSnapshotAsync(ObserverStore store, string taskId)
    {
        string now = DateTime.UtcNow.ToString("O");
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "S-" + taskId,
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SV-" + taskId,
            SubjectId = "S-" + taskId,
            Status = "Active",
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = now,
            RetiredAt = null,
        });
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
            ContentDigest = "digest-" + taskId,
            TransformTrace = null,
        };
        AnalysisTask task = AnalysisTask.Create(
            "TASK-" + taskId, "SV-" + taskId, ImmutableArray<string>.Empty, input, "SANITIZED_PERSISTENT", now);
        await store.InsertAnalysisTaskAsync(task);
        await store.InsertAnalysisResultAsync(new AnalysisResult
        {
            ResultId = "RES-" + taskId,
            TaskId = "TASK-" + taskId,
            ConclusionPayload = "{}",
            UnknownState = "UNKNOWN",
            HardGate = false,
            CreatedAt = now,
        });
    }
}
