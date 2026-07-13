using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullSpectrum.Observer.Contracts.Models;

public sealed record AuditEventContract
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/audit-event/1

    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; } // const: GLOBAL

    [JsonPropertyName("sequence_no")]
    public required long SequenceNo { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("occurred_at_utc")]
    public required string OccurredAtUtc { get; init; }

    [JsonPropertyName("actor")]
    public required JsonElement Actor { get; init; }

    [JsonPropertyName("observation_id")]
    public string? ObservationId { get; init; }

    [JsonPropertyName("operation_id")]
    public string? OperationId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("payload_digest")]
    public required string PayloadDigest { get; init; }

    [JsonPropertyName("payload_media_type")]
    public required string PayloadMediaType { get; init; }

    [JsonPropertyName("serialization_id")]
    public required string SerializationId { get; init; } // const: FS-OBS-CANON-1

    [JsonPropertyName("previous_hash")]
    public required string PreviousHash { get; init; }

    [JsonPropertyName("event_hash")]
    public required string EventHash { get; init; }

}

public sealed record EngineFacadeRequest
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; } // const: fs-observer-engine-facade/1

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("operation")]
    public required string Operation { get; init; } // const: evaluate

    [JsonPropertyName("engine")]
    public required JsonElement Engine { get; init; }

    [JsonPropertyName("seed")]
    public required long Seed { get; init; }

    [JsonPropertyName("fixed_time_utc")]
    public required string FixedTimeUtc { get; init; }

    [JsonPropertyName("scenario")]
    public required JsonElement Scenario { get; init; }

    [JsonPropertyName("output_serialization")]
    public string? OutputSerialization { get; init; } // const: FSE-PYJSON-1

}

public sealed record EngineFacadeResponse
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; } // const: fs-observer-engine-facade/1

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("engine_version")]
    public required string EngineVersion { get; init; } // const: v1.0.0

    [JsonPropertyName("engine_commit")]
    public required string EngineCommit { get; init; } // const: 09062bae2c7608bda79ee4bfde5779109e8e6197

    [JsonPropertyName("output_serialization")]
    public string? OutputSerialization { get; init; }

    [JsonPropertyName("output_sha256")]
    public string? OutputSha256 { get; init; }

    [JsonPropertyName("output")]
    public JsonElement? Output { get; init; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }

}

public sealed record FoundationAnalysisRequest
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/foundation-analysis-request/1

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("idempotency_key")]
    public required string IdempotencyKey { get; init; }

    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }

    [JsonPropertyName("requested_runtime")]
    public required JsonElement RequestedRuntime { get; init; }

    [JsonPropertyName("timeout_seconds")]
    public long? TimeoutSeconds { get; init; }

    [JsonPropertyName("submitted_at_utc")]
    public required string SubmittedAtUtc { get; init; }

}

public sealed record FoundationCasePackManifest
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/foundation-case-pack-manifest/1

    [JsonPropertyName("pack_id")]
    public required string PackId { get; init; } // const: fsp.foundation.case005

    [JsonPropertyName("version")]
    public required string Version { get; init; } // const: 1.0.0-alpha.1

    [JsonPropertyName("classification")]
    public required string Classification { get; init; } // const: SYNTHETIC

    [JsonPropertyName("engine_source")]
    public required JsonElement EngineSource { get; init; }

    [JsonPropertyName("engine_artifact")]
    public required JsonElement EngineArtifact { get; init; }

    [JsonPropertyName("case")]
    public required JsonElement Case { get; init; }

    [JsonPropertyName("schemas")]
    public required JsonElement Schemas { get; init; }

    [JsonPropertyName("files")]
    public required JsonElement Files { get; init; }

    [JsonPropertyName("manifest_sha256")]
    public required string ManifestSha256 { get; init; }

}

public sealed record FoundationErrorEnvelope
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/foundation-error-envelope/1

    [JsonPropertyName("error_id")]
    public required string ErrorId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("reason_codes")]
    public required JsonElement ReasonCodes { get; init; }

    [JsonPropertyName("occurred_at_utc")]
    public required string OccurredAtUtc { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }

}

public sealed record FoundationOperationStatus
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/foundation-operation-status/1

    [JsonPropertyName("operation_id")]
    public required string OperationId { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("observation_id")]
    public string? ObservationId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("progress_stage")]
    public string? ProgressStage { get; init; }

    [JsonPropertyName("started_at_utc")]
    public string? StartedAtUtc { get; init; }

    [JsonPropertyName("updated_at_utc")]
    public required string UpdatedAtUtc { get; init; }

    [JsonPropertyName("completed_at_utc")]
    public string? CompletedAtUtc { get; init; }

    [JsonPropertyName("reason_codes")]
    public required JsonElement ReasonCodes { get; init; }

}

public sealed record GovernanceOutputEnvelope
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/governance-output-envelope/1

    [JsonPropertyName("observation_id")]
    public required string ObservationId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("classification")]
    public required string Classification { get; init; } // const: SYNTHETIC

    [JsonPropertyName("boundary")]
    public required JsonElement Boundary { get; init; }

    [JsonPropertyName("engine")]
    public required JsonElement Engine { get; init; }

    [JsonPropertyName("runtime_snapshot_ref")]
    public required JsonElement RuntimeSnapshotRef { get; init; }

    [JsonPropertyName("engine_output")]
    public required JsonElement EngineOutput { get; init; }

    [JsonPropertyName("engine_output_sha256")]
    public required string EngineOutputSha256 { get; init; }

    [JsonPropertyName("unknowns")]
    public required JsonElement Unknowns { get; init; }

    [JsonPropertyName("evidence_refs")]
    public required JsonElement EvidenceRefs { get; init; }

    [JsonPropertyName("audit_head")]
    public required string AuditHead { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required string CreatedAtUtc { get; init; }

}

public sealed record ObservationRecord
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/observation-record/1

    [JsonPropertyName("observation_id")]
    public required string ObservationId { get; init; }

    [JsonPropertyName("operation_id")]
    public required string OperationId { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("input_sha256")]
    public required string InputSha256 { get; init; }

    [JsonPropertyName("canonical_context_sha256")]
    public string? CanonicalContextSha256 { get; init; }

    [JsonPropertyName("runtime_snapshot_id")]
    public required string RuntimeSnapshotId { get; init; }

    [JsonPropertyName("output_ref")]
    public required JsonElement? OutputRef { get; init; }

    [JsonPropertyName("audit_head")]
    public required string AuditHead { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required string CreatedAtUtc { get; init; }

    [JsonPropertyName("updated_at_utc")]
    public required string UpdatedAtUtc { get; init; }

}

public sealed record ReleaseManifest
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/release-manifest/1

    [JsonPropertyName("system_version")]
    public required string SystemVersion { get; init; } // const: 0.1.0-alpha

    [JsonPropertyName("release_commit")]
    public required string ReleaseCommit { get; init; }

    [JsonPropertyName("build")]
    public required JsonElement Build { get; init; }

    [JsonPropertyName("engine")]
    public required JsonElement Engine { get; init; }

    [JsonPropertyName("case_pack")]
    public required JsonElement CasePack { get; init; }

    [JsonPropertyName("schema_set")]
    public required JsonElement SchemaSet { get; init; }

    [JsonPropertyName("dependencies")]
    public required JsonElement Dependencies { get; init; }

    [JsonPropertyName("files")]
    public required JsonElement Files { get; init; }

    [JsonPropertyName("sbom")]
    public required JsonElement Sbom { get; init; }

    [JsonPropertyName("known_limitations")]
    public required JsonElement KnownLimitations { get; init; }

    [JsonPropertyName("manifest_sha256")]
    public required string ManifestSha256 { get; init; }

}

public sealed record RuntimeConfigurationSnapshot
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/runtime-configuration-snapshot/1

    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required string CreatedAtUtc { get; init; }

    [JsonPropertyName("engine")]
    public required JsonElement Engine { get; init; }

    [JsonPropertyName("adapter")]
    public required JsonElement Adapter { get; init; }

    [JsonPropertyName("scenario")]
    public required JsonElement Scenario { get; init; }

    [JsonPropertyName("profile")]
    public required JsonElement Profile { get; init; }

    [JsonPropertyName("knowledge")]
    public required JsonElement Knowledge { get; init; }

    [JsonPropertyName("report_template")]
    public required JsonElement ReportTemplate { get; init; }

    [JsonPropertyName("schema_set")]
    public required JsonElement SchemaSet { get; init; }

    [JsonPropertyName("serialization")]
    public required JsonElement Serialization { get; init; }

    [JsonPropertyName("snapshot_sha256")]
    public required string SnapshotSha256 { get; init; }

}

public sealed record ValidationResultContract
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; } // const: fs-observer/validation-result/1

    [JsonPropertyName("validation_kind")]
    public required string ValidationKind { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("schema_ref")]
    public JsonElement? SchemaRef { get; init; }

    [JsonPropertyName("reason_codes")]
    public required JsonElement ReasonCodes { get; init; }

    [JsonPropertyName("known_fields")]
    public JsonElement? KnownFields { get; init; }

    [JsonPropertyName("unknown_fields")]
    public JsonElement? UnknownFields { get; init; }

    [JsonPropertyName("validated_at_utc")]
    public required string ValidatedAtUtc { get; init; }

}
