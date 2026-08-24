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

public sealed class ScenarioPackPilotLoopTests
{
    private const string Now = "2026-08-25T00:00:00.0000000Z";

    [Fact]
    public async Task Batch_import_is_idempotent_and_same_key_with_different_content_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        LoadedScenarioPack pack = await fixture.InstallPackAsync();
        ScenarioPackBatchDraft firstDraft = CreateDraft(pack, "BATCH-001", "IMPORT-001", "SAMPLE-A");

        ScenarioPackBatch first = await fixture.Store.CreateScenarioPackBatchAsync(firstDraft.Batch, firstDraft.Items);
        ScenarioPackBatch retry = await fixture.Store.CreateScenarioPackBatchAsync(
            firstDraft.Batch with { BatchId = "BATCH-RETRY", CreatedAtUtc = "2026-08-25T01:00:00.0000000Z" },
            firstDraft.Items.Select(item => item with { BatchId = "BATCH-RETRY" }).ToArray());

        retry.Should().Be(first);
        (await fixture.Store.GetScenarioPackBatchItemsAsync(first.BatchId)).Should().HaveCount(1);

        ScenarioPackBatchDraft changed = CreateDraft(pack, "BATCH-002", "IMPORT-001", "SAMPLE-B");
        Func<Task> conflict = () => fixture.Store.CreateScenarioPackBatchAsync(changed.Batch, changed.Items);
        await conflict.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_BATCH_IDEMPOTENCY_CONFLICT");

        Func<Task> governanceConflict = () => fixture.Store.CreateScenarioPackBatchAsync(
            firstDraft.Batch with { BatchId = "BATCH-003", AuthorizationRef = "authorization:different" },
            firstDraft.Items.Select(item => item with { BatchId = "BATCH-003" }).ToArray());
        await governanceConflict.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_BATCH_IDEMPOTENCY_CONFLICT");

        Func<Task> invalidInitialState = () => fixture.Store.CreateScenarioPackBatchAsync(
            firstDraft.Batch with { BatchId = "BATCH-INVALID", IdempotencyKey = "IMPORT-INVALID", Status = ScenarioPackBatchStatus.Processing },
            firstDraft.Items.Select(item => item with { BatchId = "BATCH-INVALID" }).ToArray());
        await invalidInitialState.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_BATCH_INVALID");
    }

    [Fact]
    public async Task Interrupted_item_is_reclaimed_with_a_new_attempt_and_terminal_counts_are_recomputed()
    {
        await using var fixture = await Fixture.CreateAsync();
        LoadedScenarioPack pack = await fixture.InstallPackAsync();
        ScenarioPackBatchDraft draft = CreateDraft(pack, "BATCH-RECOVERY", "IMPORT-RECOVERY", "SAMPLE-A", "SAMPLE-B");
        await fixture.Store.CreateScenarioPackBatchAsync(draft.Batch, draft.Items);

        ScenarioPackBatchItem firstClaim = (await fixture.Store.ClaimNextScenarioPackBatchItemAsync(draft.Batch.BatchId, Now))!;
        firstClaim.Attempts.Should().Be(1);
        (await fixture.Store.RecoverInterruptedScenarioPackBatchAsync(draft.Batch.BatchId, Now)).Should().Be(1);

        ScenarioPackBatchItem secondClaim = (await fixture.Store.ClaimNextScenarioPackBatchItemAsync(draft.Batch.BatchId, Now))!;
        secondClaim.ItemId.Should().Be(firstClaim.ItemId);
        secondClaim.Attempts.Should().Be(2);
        await fixture.Store.CompleteScenarioPackBatchItemAsync(
            draft.Batch.BatchId, secondClaim.ItemId, new string('1', 64), Now);

        ScenarioPackBatchItem finalClaim = (await fixture.Store.ClaimNextScenarioPackBatchItemAsync(draft.Batch.BatchId, Now))!;
        await fixture.Store.FailScenarioPackBatchItemAsync(draft.Batch.BatchId, finalClaim.ItemId, "SYNTHETIC_FAILURE", Now);

        ScenarioPackBatch stored = (await fixture.Store.GetScenarioPackBatchAsync(draft.Batch.BatchId))!;
        stored.Status.Should().Be(ScenarioPackBatchStatus.Completed);
        stored.CompletedItems.Should().Be(1);
        stored.FailedItems.Should().Be(1);
        (await fixture.Store.GetScenarioPackBatchItemsAsync(draft.Batch.BatchId))
            .Sum(item => item.Status is ScenarioPackBatchItemStatus.Completed or ScenarioPackBatchItemStatus.Failed ? 1 : 0)
            .Should().Be(stored.TotalItems);
    }

    [Fact]
    public async Task Two_reviewers_are_preserved_and_review_rows_are_append_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedTaskAsync("TASK-REVIEW");
        ScenarioPackReviewRecord first = CreateReview("REVIEW-1", "TASK-REVIEW", "reviewer:alpha", "CONFIRMED");
        ScenarioPackReviewRecord second = CreateReview("REVIEW-2", "TASK-REVIEW", "reviewer:beta", "UNKNOWN");

        await fixture.Store.InsertScenarioPackReviewAsync(first);
        await fixture.Store.InsertScenarioPackReviewAsync(second);
        (await fixture.Store.GetScenarioPackReviewsAsync("TASK-REVIEW"))
            .Should().BeEquivalentTo([first, second], options => options.WithStrictOrdering());

        await fixture.AssertSqlRejectedAsync(
            "UPDATE scenario_pack_review_records SET note='changed' WHERE review_id='REVIEW-1'",
            "SCENARIO_PACK_REVIEW_IMMUTABLE");
        await fixture.AssertSqlRejectedAsync(
            "DELETE FROM scenario_pack_review_records WHERE review_id='REVIEW-2'",
            "SCENARIO_PACK_REVIEW_IMMUTABLE");
    }

    [Fact]
    public async Task Five_metrics_match_independent_numerator_and_denominator_recalculation_and_ledger_is_append_only()
    {
        ScenarioPackMetricSample[] samples =
        [
            new("S1", true, true, 10, true, true),
            new("S2", false, true, 20, false, false),
            new("S3", false, false, 30, true, null),
        ];
        IReadOnlyList<ScenarioPackMetricComputation> metrics = ScenarioPackMetricCalculator.Compute(samples);
        metrics.Should().BeEquivalentTo(
        [
            new ScenarioPackMetricComputation("discovery_rate", 1, 1, "RATIO"),
            new ScenarioPackMetricComputation("false_positive_rate", 1, 2, "RATIO"),
            new ScenarioPackMetricComputation("human_review_duration_milliseconds", 60, 3, "MEAN_MILLISECONDS"),
            new ScenarioPackMetricComputation("evidence_completeness_rate", 2, 3, "RATIO"),
            new ScenarioPackMetricComputation("repeat_result_consistency_rate", 1, 2, "RATIO"),
        ], options => options.WithStrictOrdering());

        await using var fixture = await Fixture.CreateAsync();
        LoadedScenarioPack pack = await fixture.InstallPackAsync();
        string sourceDigest = ScenarioPackMetricCalculator.ComputeSourceDigest(samples);
        for (int i = 0; i < metrics.Count; i++)
        {
            ScenarioPackMetricComputation metric = metrics[i];
            await fixture.Store.InsertScenarioPackMetricAsync(new ScenarioPackMetricLedgerEntry
            {
                EntryId = $"METRIC-{i + 1}", PackId = pack.Identity.PackId, PackVersion = pack.Identity.Version,
                PackDigest = pack.Identity.Digest, SampleBoundaryId = "BOUNDARY-001", MetricId = metric.MetricId,
                Numerator = metric.Numerator, Denominator = metric.Denominator, Unit = metric.Unit,
                SourceDigest = sourceDigest, CalculatedAtUtc = Now,
            });
        }
        (await fixture.ExecuteScalarLongAsync("SELECT count(*) FROM scenario_pack_metric_ledger")).Should().Be(5);
        await fixture.AssertSqlRejectedAsync(
            "UPDATE scenario_pack_metric_ledger SET numerator=0 WHERE entry_id='METRIC-1'",
            "SCENARIO_PACK_METRIC_IMMUTABLE");
        await fixture.AssertSqlRejectedAsync(
            "DELETE FROM scenario_pack_metric_ledger WHERE entry_id='METRIC-2'",
            "SCENARIO_PACK_METRIC_IMMUTABLE");
    }

    [Fact]
    public void Metrics_with_no_applicable_denominator_remain_unknown_instead_of_dividing_by_zero()
    {
        IReadOnlyList<ScenarioPackMetricComputation> metrics = ScenarioPackMetricCalculator.Compute(
        [
            new ScenarioPackMetricSample("S1", false, false, 0, true, null),
        ]);

        metrics.Single(metric => metric.MetricId == "discovery_rate").Value.Should().BeNull();
        metrics.Single(metric => metric.MetricId == "false_positive_rate").Value.Should().BeNull();
        metrics.Single(metric => metric.MetricId == "repeat_result_consistency_rate").Value.Should().BeNull();

        Action negativeDuration = () => ScenarioPackMetricCalculator.Compute(
            [new ScenarioPackMetricSample("S2", false, false, -1, true, null)]);
        negativeDuration.Should().Throw<ArgumentException>().WithMessage("*cannot be negative*");
    }

    [Fact]
    public void Reviewer_agreement_is_sample_bounded_and_preserves_disagreement_tasks()
    {
        ScenarioPackReviewRecord[] reviews =
        [
            CreateReview("R1", "TASK-AGREE", "reviewer:alpha", "CONFIRMED"),
            CreateReview("R2", "TASK-AGREE", "reviewer:beta", "CONFIRMED"),
            CreateReview("R3", "TASK-DISAGREE", "reviewer:alpha", "CONFIRMED"),
            CreateReview("R4", "TASK-DISAGREE", "reviewer:beta", "UNKNOWN"),
            CreateReview("R5", "TASK-SINGLE", "reviewer:alpha", "REJECTED"),
        ];

        ScenarioPackReviewerAgreement agreement = ScenarioPackReviewerAgreementCalculator.Compute(reviews);

        agreement.AgreedTasks.Should().Be(1);
        agreement.EligibleTasks.Should().Be(2);
        agreement.Value.Should().Be(0.5m);
        agreement.DisagreementTaskIds.Should().Equal("TASK-DISAGREE");
    }

    [Fact]
    public async Task Candidate_export_has_a_strict_top_level_allowlist_and_is_append_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        LoadedScenarioPack pack = await fixture.InstallPackAsync();
        await fixture.SeedTaskAsync("TASK-EXPORT");
        ScenarioPackExportRecord export = ScenarioPackExportBuilder.Build("EXPORT-1", new ScenarioPackExportInput(
            "TASK-EXPORT", pack.Identity, JsonSerializer.SerializeToElement(new { conclusion = "UNKNOWN" }),
            "INSUFFICIENT_EVIDENCE", ["evidence:E-001"], "PENDING", Now));

        using JsonDocument document = JsonDocument.Parse(export.ExportJson);
        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            "boundary", "candidate_conclusion", "contract", "created_at_utc", "evidence_refs",
            "review_status", "scenario_pack", "task_id", "unknown_state");
        await fixture.Store.InsertScenarioPackExportAsync(export);
        (await fixture.ExecuteScalarLongAsync("SELECT count(*) FROM scenario_pack_export_records")).Should().Be(1);
        await fixture.AssertSqlRejectedAsync(
            "UPDATE scenario_pack_export_records SET sensitive_scan_status='DIRTY' WHERE export_id='EXPORT-1'",
            "SCENARIO_PACK_EXPORT_IMMUTABLE");
        await fixture.AssertSqlRejectedAsync(
            "DELETE FROM scenario_pack_export_records WHERE export_id='EXPORT-1'",
            "SCENARIO_PACK_EXPORT_IMMUTABLE");
    }

    [Theory]
    [InlineData("contact user@example.com")]
    [InlineData("Authorization: Bearer secret-token")]
    [InlineData("C:\\Users\\alice\\private.txt")]
    [InlineData("access_token=secret")]
    public async Task Sensitive_candidate_content_is_blocked_before_export(string sensitiveValue)
    {
        await using var fixture = await Fixture.CreateAsync();
        LoadedScenarioPack pack = await fixture.InstallPackAsync();
        Action build = () => ScenarioPackExportBuilder.Build("EXPORT-SENSITIVE", new ScenarioPackExportInput(
            "TASK-SENSITIVE", pack.Identity, JsonSerializer.SerializeToElement(new { value = sensitiveValue }),
            "UNKNOWN", ["evidence:E-001"], "PENDING", Now));

        build.Should().Throw<InvalidDataException>().WithMessage("*SCENARIO_PACK_EXPORT_SENSITIVE_DATA*");
    }

    [Fact]
    public async Task Structured_v04_events_extend_the_global_audit_chain_and_are_append_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedTaskAsync("TASK-AUDIT");
        string[] eventTypes =
        [
            ScenarioPackAuditEventType.PackInstalled, ScenarioPackAuditEventType.TaskBound,
            ScenarioPackAuditEventType.BatchImported, ScenarioPackAuditEventType.ResultPersisted,
            ScenarioPackAuditEventType.ReviewRecorded, ScenarioPackAuditEventType.MetricRecorded,
            ScenarioPackAuditEventType.ExportRecorded,
        ];
        string? previous = null;
        for (int index = 0; index < eventTypes.Length; index++)
        {
            string auditId = $"AUD-V04-{index + 1:D2}";
            ScenarioPackAuditAppend append = ScenarioPackAuditFactory.Create(
                $"EVENT-V04-{index + 1:D2}", auditId, eventTypes[index], $"entity:{index + 1}",
                new string("abcdef1"[index], 64), index == 0 ? null : "TASK-AUDIT",
                $"2026-08-25T00:00:{index:D2}.0000000Z", "test-user", "test-machine", "test-session", previous);
            await fixture.Store.AppendScenarioPackAuditEventAsync(append.Event, append.Audit);
            previous = auditId;
        }

        (await fixture.Store.GetScenarioPackAuditEventsAsync()).Select(item => item.EventType)
            .Should().Equal(eventTypes);
        AuditChainVerification verification = await fixture.Store.VerifyAuditChainAsync();
        verification.IsValid.Should().BeTrue();
        verification.RecordCount.Should().Be(7);
        await fixture.AssertSqlRejectedAsync(
            "UPDATE scenario_pack_audit_events SET entity_ref='changed' WHERE event_id='EVENT-V04-01'",
            "SCENARIO_PACK_AUDIT_EVENT_IMMUTABLE");
        await fixture.AssertSqlRejectedAsync(
            "DELETE FROM scenario_pack_audit_events WHERE event_id='EVENT-V04-07'",
            "SCENARIO_PACK_AUDIT_EVENT_IMMUTABLE");

        ScenarioPackAuditAppend stale = ScenarioPackAuditFactory.Create(
            "EVENT-STALE", "AUD-STALE", ScenarioPackAuditEventType.ExportRecorded, "entity:stale",
            new string('f', 64), "TASK-AUDIT", "2026-08-25T00:01:00.0000000Z",
            "test-user", "test-machine", "test-session", "AUD-V04-01");
        Func<Task> appendStale = () => fixture.Store.AppendScenarioPackAuditEventAsync(stale.Event, stale.Audit);
        await appendStale.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_AUDIT_CHAIN_CONFLICT");

        ScenarioPackAuditAppend earlierTime = ScenarioPackAuditFactory.Create(
            "EVENT-EARLY", "AUD-EARLY", ScenarioPackAuditEventType.ExportRecorded, "entity:early",
            new string('e', 64), "TASK-AUDIT", "2026-08-25T00:00:05.5000000Z",
            "test-user", "test-machine", "test-session", "AUD-V04-07");
        Func<Task> appendEarlier = () => fixture.Store.AppendScenarioPackAuditEventAsync(earlierTime.Event, earlierTime.Audit);
        await appendEarlier.Should().ThrowAsync<StoreException>()
            .Where(error => error.ReasonCode == "SCENARIO_PACK_AUDIT_TIME_CONFLICT");
    }

    private static ScenarioPackBatchDraft CreateDraft(
        LoadedScenarioPack pack, string batchId, string idempotencyKey, params string[] sampleIds)
    {
        IReadOnlyList<(string ItemId, string InputJson)> inputs = sampleIds.Select((sampleId, index) =>
            ($"ITEM-{index + 1}", JsonSerializer.Serialize(new
            {
                sample_id = sampleId,
                sources = new[]
                {
                    new { source_id = "source-a", version = "1.0.0", claim = "Approval is required." },
                    new { source_id = "source-b", version = "2.0.0", claim = "Approval is not required." },
                },
            }))).ToArray();
        return ScenarioPackBatchFactory.Create(
            pack, batchId, idempotencyKey, inputs,
            "authorization:synthetic-only", "redaction:CLEAN", "deletion:after-pilot", Now);
    }

    private static ScenarioPackReviewRecord CreateReview(
        string reviewId, string taskId, string reviewer, string disposition) =>
        ScenarioPackReviewFactory.Create(
            reviewId, taskId, reviewer, disposition, ["evidence:E-001"], "synthetic review",
            Now, "2026-08-25T00:00:01.0000000Z");

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string dbPath, ObserverStore store)
        {
            DbPath = dbPath;
            Store = store;
        }

        public string DbPath { get; }
        public ObserverStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"observer-v04-pilot-{Guid.NewGuid():N}.db");
            var store = new ObserverStore(dbPath);
            await store.EnsureSchemaAsync();
            return new Fixture(dbPath, store);
        }

        public async Task<LoadedScenarioPack> InstallPackAsync()
        {
            string root = FindRepositoryRoot();
            var loader = new ScenarioPackLoader(
                Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
                Path.Combine(root, "config", "scenario-pack-trust-roots.json"),
                "v0.4.0-beta", "v1.5.0");
            LoadedScenarioPack pack = loader.Load(Path.Combine(
                root, "packs", "scenario-packs", "knowledge-conflict", "1.0.0"));
            await Store.InstallScenarioPackAsync(new ScenarioPackRegistration
            {
                PackId = pack.Identity.PackId, Version = pack.Identity.Version, Digest = pack.Identity.Digest,
                ManifestJson = JsonSerializer.Serialize(pack.Manifest, FoundationJson.CreateOptions()), InstalledAtUtc = Now,
            });
            return pack;
        }

        public async Task SeedTaskAsync(string taskId)
        {
            string subjectId = "SUBJ-" + taskId;
            string versionId = "SUBV-" + taskId;
            await Store.InsertSubjectAsync(new ObservedSubject
            {
                LocalSubjectId = subjectId, SubjectType = "SYNTHETIC", Mode = "V04_TEST", CreatedAt = Now,
            });
            await Store.InsertSubjectVersionAsync(new SubjectVersion
            {
                VersionId = versionId, SubjectId = subjectId, Status = "Active", Seq = 1,
                Payload = "{\"subject_type\":\"SYNTHETIC\",\"mode\":\"V04_TEST\"}",
                SchemaVersion = "test/1", CreatedAt = Now, ActiveFrom = Now,
            });
            await Store.InsertAnalysisTaskAsync(AnalysisTask.Create(
                taskId, versionId, ImmutableArray<string>.Empty,
                new RawAnalysisInput
                {
                    Mode = "FORM", CanonicalInput = "{}", ContentDigest = new string('a', 64), TransformTrace = "[]",
                },
                "SANITIZED_PERSISTENT", Now));
        }

        public async Task AssertSqlRejectedAsync(string sql, string marker)
        {
            await using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=false;");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Func<Task> execute = async () => { _ = await command.ExecuteNonQueryAsync(); };
            await execute.Should().ThrowAsync<SqliteException>().WithMessage($"*{marker}*");
        }

        public async Task<long> ExecuteScalarLongAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=false;");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(DbPath)) File.Delete(DbPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "baselines.lock.json"))) return cursor.FullName;
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
