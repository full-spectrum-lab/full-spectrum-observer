using System.Text.Json;
using System.Text.Json.Serialization;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// The frozen Engine v1.5.0 data contract between the Observer Console and the Engine
/// (python -m governance_chain). All pinned identity values are imported here and in
/// <c>appsettings.json</c> (R1-B §10.2). The Observer NEVER negotiates the version.
/// </summary>
public static class EngineV15Contract
{
    /// <summary>Engine release tag, pinned to v1.5.0 (ADR-003 anchors v1.5).</summary>
    public const string EngineTag = "v1.5.0";

    /// <summary>
    /// Engine v1.5.0 commit (Gitee authoritative anchor, sign-off @ 2026-07-15).
    /// GitHub equivalent ab9939b2... is a fork of the same source tree and is non-blocking.
    /// </summary>
    public const string EngineCommit = "88493007d4e00344c70a70ed0e5a5d652dec86f5";

    /// <summary>
    /// Engine v1.5.0 release artifact sha256. NOT available in this workspace (the published
    /// binary is not present), so this is an explicit PLACEHOLDER. It MUST be filled from the
    /// published artifact before GO-6 sign-off. Do NOT fabricate a real sha256 here.
    /// </summary>
    public const string EngineArtifactDigest = "PLACEHOLDER_PENDING_PUBLISHED_ARTIFACT_SHA256";

    /// <summary>Observer-side Adapter fixture version compatible with the v1.5 matrix.</summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>Compatibility matrix id anchored to the v1.5 matrix (not v1.4). ADR-003.</summary>
    public const string CompatibilityMatrixId = "FS-OBS-CM-V15";

    /// <summary>Envelope version for the EngineRequest/EngineResponse envelopes.</summary>
    public const string EnvelopeVersion = "1.0";

    /// <summary>Observer schema version, backed by the canonical Init.sql.</summary>
    public static string SchemaVersion => SchemaDefinition.Version;

    /// <summary>Observer schema digest (sha256 of Init.sql), computed — never fabricated.</summary>
    public static string SchemaDigest => SchemaDefinition.Digest;

    /// <summary>Analyzers / profiles / engine are pinned; align with appsettings.json at deploy.</summary>
    public const string AnalyzerVersion = "1.5.0";
    public const string ProfileVersion = "1.5.0";

    /// <summary>Serialization options for the Engine envelopes (explicit property names, lenient).</summary>
    public static JsonSerializerOptions EnvelopeOptions => new() { MaxDepth = 64, PropertyNameCaseInsensitive = false };
}

// ---------------------------------------------------------------------------
// Request envelope (Console -> Engine v1.5.0)
// ---------------------------------------------------------------------------

/// <summary>Subject declaration sub-object of <see cref="EngineRequest"/>. Analysis context only.</summary>
public sealed record EngineSubject
{
    [JsonPropertyName("local_subject_id")] public required string LocalSubjectId { get; init; }
    [JsonPropertyName("subject_type")] public required string SubjectType { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("concentration_tier")] public string? ConcentrationTier { get; init; }
    [JsonPropertyName("declaration")] public JsonElement Declaration { get; init; }
}

/// <summary>Knowledge source version reference sub-object of <see cref="EngineRequest"/>.</summary>
public sealed record EngineKnowledge
{
    [JsonPropertyName("source_id")] public required string SourceId { get; init; }
    [JsonPropertyName("version_id")] public required string VersionId { get; init; }
    [JsonPropertyName("digest")] public required string Digest { get; init; }
    [JsonPropertyName("applicability")] public required string Applicability { get; init; }
}

/// <summary>Raw input sub-object of <see cref="EngineRequest"/> (three intake modes normalized).</summary>
public sealed record EngineInput
{
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("canonical_input")] public JsonElement CanonicalInput { get; init; }
    [JsonPropertyName("content_digest")] public required string ContentDigest { get; init; }
    [JsonPropertyName("transform_trace")] public JsonElement TransformTrace { get; init; }
}

/// <summary>The Engine v1.5.0 request envelope. Structure only — no governance judgement.</summary>
public sealed record EngineRequest
{
    [JsonPropertyName("envelope_version")] public required string EnvelopeVersion { get; init; }
    [JsonPropertyName("analyzer_version")] public required string AnalyzerVersion { get; init; }
    [JsonPropertyName("engine_version")] public required string EngineVersion { get; init; }
    [JsonPropertyName("engine_commit")] public required string EngineCommit { get; init; }
    [JsonPropertyName("profile_version")] public required string ProfileVersion { get; init; }
    [JsonPropertyName("schema_version")] public required string SchemaVersion { get; init; }
    [JsonPropertyName("schema_digest")] public required string SchemaDigest { get; init; }
    [JsonPropertyName("case_id")] public required string CaseId { get; init; }
    [JsonPropertyName("subject")] public required EngineSubject Subject { get; init; }
    [JsonPropertyName("knowledge")] public required List<EngineKnowledge> Knowledge { get; init; }
    [JsonPropertyName("input")] public required EngineInput Input { get; init; }
    [JsonPropertyName("retention_mode")] public required string RetentionMode { get; init; }
}

// ---------------------------------------------------------------------------
// Response envelope (Engine v1.5.0 -> Console)
// ---------------------------------------------------------------------------

/// <summary>Replay anchor sub-object of <see cref="EngineResponse"/> (red line #8).</summary>
public sealed record EngineReplayRef
{
    [JsonPropertyName("digest")] public required string Digest { get; init; }
    [JsonPropertyName("engine_version")] public required string EngineVersion { get; init; }
}

/// <summary>Evidence bundle sub-object of <see cref="EngineResponse"/> (red line #8).</summary>
public sealed record EngineEvidence
{
    [JsonPropertyName("evidence_digest")] public required string EvidenceDigest { get; init; }
    [JsonPropertyName("references")] public required List<string> References { get; init; }
}

/// <summary>A single conflict observation as returned by the Engine (pass-through, no recompute).</summary>
public sealed record EngineConflictObservation
{
    [JsonPropertyName("conflict_type")] public required string ConflictType { get; init; }
    [JsonPropertyName("involved_subjects")] public required List<string> InvolvedSubjects { get; init; }
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("human_review_required")] public required bool HumanReviewRequired { get; init; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; init; }
    [JsonPropertyName("missing_context")] public List<string>? MissingContext { get; init; }
}

/// <summary>The Engine v1.5.0 response envelope. All fields are passed through verbatim.</summary>
public sealed record EngineResponse
{
    [JsonPropertyName("engine_version")] public required string EngineVersion { get; init; }
    [JsonPropertyName("engine_commit")] public required string EngineCommit { get; init; }
    [JsonPropertyName("schema_version")] public required string SchemaVersion { get; init; }
    [JsonPropertyName("schema_digest")] public required string SchemaDigest { get; init; }
    [JsonPropertyName("analyzer_version")] public required string AnalyzerVersion { get; init; }
    [JsonPropertyName("profile_version")] public required string ProfileVersion { get; init; }
    [JsonPropertyName("conclusion")] public JsonElement? Conclusion { get; init; }
    [JsonPropertyName("conflict_observations")] public List<EngineConflictObservation>? ConflictObservations { get; init; }
    [JsonPropertyName("unknown_state")] public required string UnknownState { get; init; }
    [JsonPropertyName("hard_gate")] public required bool HardGate { get; init; }
    [JsonPropertyName("runtime_digest")] public required string RuntimeDigest { get; init; }
    [JsonPropertyName("replay_ref")] public EngineReplayRef? ReplayRef { get; init; }
    [JsonPropertyName("evidence")] public EngineEvidence? Evidence { get; init; }
}
