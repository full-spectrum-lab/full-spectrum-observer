using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class ScenarioPackPersistenceTests
{
    [Fact]
    public async Task Installed_pack_survives_store_restart_and_conflicting_digest_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        ScenarioPackRegistration registration = LoadRegistration("knowledge-conflict");

        ScenarioPackRegistration installed = await fixture.Store.InstallScenarioPackAsync(registration);
        ScenarioPackRegistration repeated = await fixture.Store.InstallScenarioPackAsync(
            registration with { InstalledAtUtc = "2026-08-25T01:00:00.0000000Z" });

        repeated.Should().Be(installed);
        await fixture.ReopenAsync();
        (await fixture.Store.GetScenarioPackAsync(registration.PackId, registration.Version))
            .Should().Be(installed);

        Func<Task> conflict = () => fixture.Store.InstallScenarioPackAsync(
            registration with { Digest = new string('f', 64) });
        await conflict.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_IDENTITY_CONFLICT");
    }

    [Fact]
    public async Task Task_binding_survives_restart_is_idempotent_and_cannot_be_rebound()
    {
        await using var fixture = await Fixture.CreateAsync();
        ScenarioPackRegistration knowledgeConflict = LoadRegistration("knowledge-conflict");
        ScenarioPackRegistration policyConflict = LoadRegistration("synthetic-policy-conflict");
        await fixture.Store.InstallScenarioPackAsync(knowledgeConflict);
        await fixture.Store.InstallScenarioPackAsync(policyConflict);
        await SeedTaskAsync(fixture.Store, "TASK-V04-001");

        var binding = new ScenarioPackTaskBinding
        {
            TaskId = "TASK-V04-001",
            PackId = knowledgeConflict.PackId,
            PackVersion = knowledgeConflict.Version,
            PackDigest = knowledgeConflict.Digest,
            ProfileRefsJson = "[\"profiles/default.profile.json\"]",
            BoundAtUtc = "2026-08-25T00:00:00.0000000Z",
        };
        ScenarioPackTaskBinding first = await fixture.Store.BindScenarioPackToTaskAsync(binding);
        ScenarioPackTaskBinding retry = await fixture.Store.BindScenarioPackToTaskAsync(
            binding with { BoundAtUtc = "2026-08-25T01:00:00.0000000Z" });

        retry.Should().Be(first);
        await fixture.ReopenAsync();
        (await fixture.Store.GetScenarioPackTaskBindingAsync(binding.TaskId)).Should().Be(first);

        Func<Task> rebind = () => fixture.Store.BindScenarioPackToTaskAsync(binding with
        {
            PackId = policyConflict.PackId,
            PackDigest = policyConflict.Digest,
        });
        await rebind.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_TASK_BINDING_CONFLICT");
    }

    [Fact]
    public async Task Database_triggers_reject_pack_or_binding_mutation_and_deletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        ScenarioPackRegistration registration = LoadRegistration("knowledge-conflict");
        await fixture.Store.InstallScenarioPackAsync(registration);
        await SeedTaskAsync(fixture.Store, "TASK-V04-IMMUTABLE");
        await fixture.Store.BindScenarioPackToTaskAsync(new ScenarioPackTaskBinding
        {
            TaskId = "TASK-V04-IMMUTABLE",
            PackId = registration.PackId,
            PackVersion = registration.Version,
            PackDigest = registration.Digest,
            ProfileRefsJson = "[\"profiles/default.profile.json\"]",
            BoundAtUtc = "2026-08-25T00:00:00.0000000Z",
        });

        await using var connection = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=false;");
        await connection.OpenAsync();
        await AssertSqlRejectedAsync(connection,
            "UPDATE scenario_pack_versions SET installed_at_utc='changed' WHERE pack_id='observer.case.knowledge-conflict'",
            "SCENARIO_PACK_VERSION_IMMUTABLE");
        await AssertSqlRejectedAsync(connection,
            "DELETE FROM scenario_pack_task_bindings WHERE task_id='TASK-V04-IMMUTABLE'",
            "SCENARIO_PACK_TASK_BINDING_IMMUTABLE");
    }

    private static async Task AssertSqlRejectedAsync(SqliteConnection connection, string sql, string marker)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Func<Task> execute = async () => { _ = await command.ExecuteNonQueryAsync(); };
        await execute.Should().ThrowAsync<SqliteException>().WithMessage($"*{marker}*");
    }

    private static ScenarioPackRegistration LoadRegistration(string packName)
    {
        string root = FindRepositoryRoot();
        string packPath = Path.Combine(root, "packs", "scenario-packs", packName, "1.0.0");
        var loader = new ScenarioPackLoader(
            Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
            Path.Combine(root, "config", "scenario-pack-trust-roots.json"),
            "v0.4.0-beta",
            "v1.5.0");
        LoadedScenarioPack loaded = loader.Load(packPath);
        return new ScenarioPackRegistration
        {
            PackId = loaded.Identity.PackId,
            Version = loaded.Identity.Version,
            Digest = loaded.Identity.Digest,
            ManifestJson = JsonSerializer.Serialize(loaded.Manifest, FoundationJson.CreateOptions()),
            InstalledAtUtc = "2026-08-25T00:00:00.0000000Z",
        };
    }

    private static async Task SeedTaskAsync(ObserverStore store, string taskId)
    {
        string subjectId = "SUBJ-" + taskId;
        string versionId = "SUBV-" + taskId;
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = subjectId,
            SubjectType = "SYNTHETIC",
            Mode = "V04_TEST",
            CreatedAt = "2026-08-25T00:00:00.0000000Z",
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = versionId,
            SubjectId = subjectId,
            Status = "Active",
            Seq = 1,
            Payload = "{\"subject_type\":\"SYNTHETIC\",\"mode\":\"V04_TEST\"}",
            SchemaVersion = "test/1",
            CreatedAt = "2026-08-25T00:00:00.0000000Z",
            ActiveFrom = "2026-08-25T00:00:00.0000000Z",
        });
        await store.InsertAnalysisTaskAsync(AnalysisTask.Create(
            taskId,
            versionId,
            ImmutableArray<string>.Empty,
            new RawAnalysisInput
            {
                Mode = "FORM",
                CanonicalInput = "{}",
                ContentDigest = new string('a', 64),
                TransformTrace = "[]",
            },
            "SANITIZED_PERSISTENT",
            "2026-08-25T00:00:00.0000000Z"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "baselines.lock.json")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string dbPath, ObserverStore store)
        {
            DbPath = dbPath;
            Store = store;
        }

        public string DbPath { get; }
        public ObserverStore Store { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"observer-v04-{Guid.NewGuid():N}.db");
            var store = new ObserverStore(dbPath);
            await store.EnsureSchemaAsync();
            return new Fixture(dbPath, store);
        }

        public async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Store = new ObserverStore(DbPath);
            await Store.EnsureSchemaAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(DbPath))
            {
                File.Delete(DbPath);
            }
        }
    }
}
