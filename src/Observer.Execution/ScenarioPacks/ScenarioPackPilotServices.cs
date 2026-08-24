using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Schema;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed record ScenarioPackBatchDraft(ScenarioPackBatch Batch, IReadOnlyList<ScenarioPackBatchItem> Items);

public static class ScenarioPackBatchFactory
{
    public static ScenarioPackBatchDraft Create(
        LoadedScenarioPack pack,
        string batchId,
        string idempotencyKey,
        IReadOnlyList<(string ItemId, string InputJson)> inputs,
        string authorizationRef,
        string redactionReportRef,
        string deletionPlanRef,
        string createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (inputs.Count == 0 || inputs.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
            throw new ArgumentException("A batch requires uniquely identified input items.", nameof(inputs));
        RequireReference(authorizationRef, nameof(authorizationRef));
        RequireReference(redactionReportRef, nameof(redactionReportRef));
        RequireReference(deletionPlanRef, nameof(deletionPlanRef));

        string schemaPath = Path.Combine(pack.RootDirectory, pack.Manifest.InputSchemaRef.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var items = new List<ScenarioPackBatchItem>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            using JsonDocument input = JsonDocument.Parse(inputs[i].InputJson);
            SchemaValidationReport report = new FoundationSchemaValidator().Validate(schema.RootElement, input.RootElement);
            if (!report.IsValid)
            {
                throw new ScenarioPackLoadException(
                    ScenarioPackReasonCodes.ManifestInvalid,
                    $"Batch item {inputs[i].ItemId} does not match the Pack input Schema.");
            }
            byte[] canonical = FsObsCanonicalizer.Canonicalize(input.RootElement);
            items.Add(new ScenarioPackBatchItem
            {
                BatchId = batchId,
                ItemId = inputs[i].ItemId,
                SequenceNo = i + 1,
                InputJson = Encoding.UTF8.GetString(canonical),
                InputDigest = FsObsCanonicalizer.Sha256Hex(canonical),
                Status = ScenarioPackBatchItemStatus.Pending,
                Attempts = 0,
            });
        }
        byte[] aggregate = Encoding.UTF8.GetBytes(string.Join("\n", items.Select(item => $"{item.SequenceNo}:{item.ItemId}:{item.InputDigest}")));
        var batch = new ScenarioPackBatch
        {
            BatchId = batchId,
            IdempotencyKey = idempotencyKey,
            PackId = pack.Identity.PackId,
            PackVersion = pack.Identity.Version,
            PackDigest = pack.Identity.Digest,
            InputDigest = Convert.ToHexStringLower(SHA256.HashData(aggregate)),
            AuthorizationRef = authorizationRef,
            RedactionReportRef = redactionReportRef,
            DeletionPlanRef = deletionPlanRef,
            Status = ScenarioPackBatchStatus.Pending,
            TotalItems = items.Count,
            CompletedItems = 0,
            FailedItems = 0,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
        return new ScenarioPackBatchDraft(batch, items);
    }

    private static void RequireReference(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Pilot governance reference is required.", name);
    }
}

public static class ScenarioPackReviewFactory
{
    public static ScenarioPackReviewRecord Create(
        string reviewId,
        string taskId,
        string reviewerRef,
        string disposition,
        IReadOnlyList<string> evidenceRefs,
        string note,
        string startedAtUtc,
        string completedAtUtc)
    {
        if (disposition is not ("CONFIRMED" or "REJECTED" or "UNKNOWN"))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        if (string.IsNullOrWhiteSpace(reviewerRef)) throw new ArgumentException("Reviewer reference is required.", nameof(reviewerRef));
        if (evidenceRefs.Count == 0 || evidenceRefs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one evidence reference is required.", nameof(evidenceRefs));
        DateTimeOffset started = DateTimeOffset.Parse(startedAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset completed = DateTimeOffset.Parse(completedAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        if (completed < started) throw new ArgumentException("Review completion precedes review start.", nameof(completedAtUtc));
        long duration = checked((long)(completed - started).TotalMilliseconds);
        string evidenceJson = JsonSerializer.Serialize(evidenceRefs);
        var material = new
        {
            review_id = reviewId, task_id = taskId, reviewer_ref = reviewerRef, disposition,
            evidence_refs = evidenceRefs, note, duration_milliseconds = duration,
            started_at_utc = startedAtUtc, completed_at_utc = completedAtUtc,
        };
        byte[] canonical = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(material));
        return new ScenarioPackReviewRecord
        {
            ReviewId = reviewId, TaskId = taskId, ReviewerRef = reviewerRef, Disposition = disposition,
            EvidenceRefsJson = evidenceJson, Note = note, DurationMilliseconds = duration,
            StartedAtUtc = startedAtUtc, CompletedAtUtc = completedAtUtc,
            RecordDigest = FsObsCanonicalizer.Sha256Hex(canonical),
        };
    }
}

public sealed record ScenarioPackMetricSample(
    string SampleId,
    bool LabelledConflict,
    bool CandidateConflict,
    long ReviewDurationMilliseconds,
    bool EvidenceComplete,
    bool? RepeatResultConsistent);

public sealed record ScenarioPackMetricComputation(string MetricId, long Numerator, long Denominator, string Unit)
{
    public decimal? Value => Denominator == 0 ? null : decimal.Divide(Numerator, Denominator);
}

public static class ScenarioPackMetricCalculator
{
    public static IReadOnlyList<ScenarioPackMetricComputation> Compute(IReadOnlyList<ScenarioPackMetricSample> samples)
    {
        if (samples.Count == 0 || samples.Select(sample => sample.SampleId).Distinct(StringComparer.Ordinal).Count() != samples.Count)
            throw new ArgumentException("A non-empty, uniquely identified labelled sample is required.", nameof(samples));
        if (samples.Any(sample => sample.ReviewDurationMilliseconds < 0))
            throw new ArgumentException("Review duration cannot be negative.", nameof(samples));
        long labelledConflicts = samples.LongCount(sample => sample.LabelledConflict);
        long truePositives = samples.LongCount(sample => sample.LabelledConflict && sample.CandidateConflict);
        long candidates = samples.LongCount(sample => sample.CandidateConflict);
        long falsePositives = samples.LongCount(sample => !sample.LabelledConflict && sample.CandidateConflict);
        ScenarioPackMetricSample[] replayed = samples.Where(sample => sample.RepeatResultConsistent.HasValue).ToArray();
        return
        [
            new("discovery_rate", truePositives, labelledConflicts, "RATIO"),
            new("false_positive_rate", falsePositives, candidates, "RATIO"),
            new("human_review_duration_milliseconds", samples.Sum(sample => sample.ReviewDurationMilliseconds), samples.Count, "MEAN_MILLISECONDS"),
            new("evidence_completeness_rate", samples.LongCount(sample => sample.EvidenceComplete), samples.Count, "RATIO"),
            new("repeat_result_consistency_rate", replayed.LongCount(sample => sample.RepeatResultConsistent is true), replayed.LongLength, "RATIO"),
        ];
    }

    public static string ComputeSourceDigest(IReadOnlyList<ScenarioPackMetricSample> samples) =>
        FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(
            samples.OrderBy(sample => sample.SampleId, StringComparer.Ordinal))));
}

public sealed record ScenarioPackReviewerAgreement(int AgreedTasks, int EligibleTasks, IReadOnlyList<string> DisagreementTaskIds)
{
    public decimal? Value => EligibleTasks == 0 ? null : decimal.Divide(AgreedTasks, EligibleTasks);
}

public static class ScenarioPackReviewerAgreementCalculator
{
    public static ScenarioPackReviewerAgreement Compute(IReadOnlyList<ScenarioPackReviewRecord> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        IGrouping<string, ScenarioPackReviewRecord>[] eligible = reviews
            .GroupBy(review => review.TaskId, StringComparer.Ordinal)
            .Where(group => group.Select(review => review.ReviewerRef).Distinct(StringComparer.Ordinal).Count() >= 2)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        string[] disagreements = eligible
            .Where(group => group.Select(review => review.Disposition).Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        return new ScenarioPackReviewerAgreement(eligible.Length - disagreements.Length, eligible.Length, disagreements);
    }
}

public sealed record ScenarioPackExportInput(
    string TaskId,
    ScenarioPackIdentity PackIdentity,
    JsonElement CandidateConclusion,
    string UnknownState,
    IReadOnlyList<string> EvidenceRefs,
    string ReviewStatus,
    string CreatedAtUtc);

public static partial class ScenarioPackExportBuilder
{
    private static readonly Regex SensitivePattern = BuildSensitivePattern();

    public static ScenarioPackExportRecord Build(string exportId, ScenarioPackExportInput input)
    {
        var document = new
        {
            contract = "fs-observer/scenario-pack-candidate-export/1",
            task_id = input.TaskId,
            scenario_pack = new
            {
                pack_id = input.PackIdentity.PackId,
                version = input.PackIdentity.Version,
                digest = input.PackIdentity.Digest,
            },
            candidate_conclusion = input.CandidateConclusion,
            unknown_state = input.UnknownState,
            evidence_refs = input.EvidenceRefs,
            review_status = input.ReviewStatus,
            boundary = new
            {
                candidate_only = true,
                human_review_required = true,
                authorized_action = false,
                sample_limited = true,
            },
            created_at_utc = input.CreatedAtUtc,
        };
        byte[] canonical = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(document));
        string json = Encoding.UTF8.GetString(canonical);
        if (SensitivePattern.IsMatch(json))
            throw new InvalidDataException("SCENARIO_PACK_EXPORT_SENSITIVE_DATA: export failed the sensitive-data scan.");
        return new ScenarioPackExportRecord
        {
            ExportId = exportId,
            TaskId = input.TaskId,
            ExportJson = json,
            ExportDigest = FsObsCanonicalizer.Sha256Hex(canonical),
            SensitiveScanStatus = "CLEAN",
            CreatedAtUtc = input.CreatedAtUtc,
        };
    }

    [GeneratedRegex(@"(?i)(?:bearer\s+[a-z0-9._-]+|api[_-]?key[^a-z0-9]{0,4}[:=]|access[_-]?token[^a-z0-9]{0,4}[:=]|[a-z]:\\+users\\+|[\w.+-]+@[\w.-]+\.[a-z]{2,})", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BuildSensitivePattern();
}

public sealed record ScenarioPackAuditAppend(ScenarioPackAuditEvent Event, AuditRecord Audit);

public static class ScenarioPackAuditFactory
{
    public static ScenarioPackAuditAppend Create(
        string eventId,
        string auditId,
        string eventType,
        string entityRef,
        string entityDigest,
        string? taskId,
        string occurredAtUtc,
        string windowsUser,
        string machine,
        string session,
        string? previousAuditId)
    {
        string[] allowed =
        [
            ScenarioPackAuditEventType.PackInstalled, ScenarioPackAuditEventType.TaskBound,
            ScenarioPackAuditEventType.BatchImported, ScenarioPackAuditEventType.ResultPersisted,
            ScenarioPackAuditEventType.ReviewRecorded, ScenarioPackAuditEventType.MetricRecorded,
            ScenarioPackAuditEventType.ExportRecorded,
        ];
        if (!allowed.Contains(eventType, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(eventType));
        if (entityDigest.Length != 64 || entityDigest.Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(entityDigest, entityDigest.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("Entity digest must be lowercase SHA-256 text.", nameof(entityDigest));
        string action = "V04_" + eventType;
        byte[] material = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            previous_audit_id = previousAuditId,
            event_id = eventId,
            audit_id = auditId,
            event_type = eventType,
            entity_ref = entityRef,
            entity_digest = entityDigest,
            task_id = taskId,
            occurred_at_utc = occurredAtUtc,
            windows_user = windowsUser,
            machine,
            session,
        }));
        var auditEvent = new ScenarioPackAuditEvent
        {
            EventId = eventId, AuditId = auditId, EventType = eventType, EntityRef = entityRef,
            EntityDigest = entityDigest, TaskId = taskId, OccurredAtUtc = occurredAtUtc,
        };
        AuditRecord audit = AuditRecord.Append(
            auditId, taskId, action, windowsUser, machine, session, occurredAtUtc,
            FsObsCanonicalizer.Sha256Hex(material), previousAuditId);
        return new ScenarioPackAuditAppend(auditEvent, audit);
    }
}
