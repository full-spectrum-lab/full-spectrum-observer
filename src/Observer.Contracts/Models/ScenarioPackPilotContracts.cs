namespace FullSpectrum.Observer.Contracts.Models;

public static class ScenarioPackBatchStatus
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Interrupted = "INTERRUPTED";
    public const string Completed = "COMPLETED";
}

public static class ScenarioPackBatchItemStatus
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

public sealed record ScenarioPackBatch
{
    public required string BatchId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required string PackDigest { get; init; }
    public required string InputDigest { get; init; }
    public required string AuthorizationRef { get; init; }
    public required string RedactionReportRef { get; init; }
    public required string DeletionPlanRef { get; init; }
    public required string Status { get; init; }
    public required int TotalItems { get; init; }
    public required int CompletedItems { get; init; }
    public required int FailedItems { get; init; }
    public required string CreatedAtUtc { get; init; }
    public required string UpdatedAtUtc { get; init; }
}

public sealed record ScenarioPackBatchItem
{
    public required string BatchId { get; init; }
    public required string ItemId { get; init; }
    public required int SequenceNo { get; init; }
    public required string InputJson { get; init; }
    public required string InputDigest { get; init; }
    public required string Status { get; init; }
    public required int Attempts { get; init; }
    public string? ResultDigest { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record ScenarioPackReviewRecord
{
    public required string ReviewId { get; init; }
    public required string TaskId { get; init; }
    public required string ReviewerRef { get; init; }
    public required string Disposition { get; init; }
    public required string EvidenceRefsJson { get; init; }
    public required string Note { get; init; }
    public required long DurationMilliseconds { get; init; }
    public required string StartedAtUtc { get; init; }
    public required string CompletedAtUtc { get; init; }
    public required string RecordDigest { get; init; }
}

public sealed record ScenarioPackMetricLedgerEntry
{
    public required string EntryId { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required string PackDigest { get; init; }
    public required string SampleBoundaryId { get; init; }
    public required string MetricId { get; init; }
    public required long Numerator { get; init; }
    public required long Denominator { get; init; }
    public required string Unit { get; init; }
    public required string SourceDigest { get; init; }
    public required string CalculatedAtUtc { get; init; }
}

public sealed record ScenarioPackExportRecord
{
    public required string ExportId { get; init; }
    public required string TaskId { get; init; }
    public required string ExportJson { get; init; }
    public required string ExportDigest { get; init; }
    public required string SensitiveScanStatus { get; init; }
    public required string CreatedAtUtc { get; init; }
}

public static class ScenarioPackAuditEventType
{
    public const string PackInstalled = "PACK_INSTALLED";
    public const string TaskBound = "TASK_BOUND";
    public const string BatchImported = "BATCH_IMPORTED";
    public const string ResultPersisted = "RESULT_PERSISTED";
    public const string ReviewRecorded = "REVIEW_RECORDED";
    public const string MetricRecorded = "METRIC_RECORDED";
    public const string ExportRecorded = "EXPORT_RECORDED";
}

public sealed record ScenarioPackAuditEvent
{
    public required string EventId { get; init; }
    public required string AuditId { get; init; }
    public required string EventType { get; init; }
    public required string EntityRef { get; init; }
    public required string EntityDigest { get; init; }
    public string? TaskId { get; init; }
    public required string OccurredAtUtc { get; init; }
}
