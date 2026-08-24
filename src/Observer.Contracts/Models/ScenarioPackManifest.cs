using System.Text.Json.Serialization;

namespace FullSpectrum.Observer.Contracts.Models;

public static class ScenarioPackContract
{
    public const string ManifestV1 = "fs-observer/scenario-pack-manifest/1";
    public const string ManifestFileName = "scenario-pack.manifest.json";
}

public sealed record ScenarioPackManifest
{
    [JsonPropertyName("contract")]
    public required string Contract { get; init; }

    [JsonPropertyName("pack_id")]
    public required string PackId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    [JsonPropertyName("compatible_observer_versions")]
    public required string[] CompatibleObserverVersions { get; init; }

    [JsonPropertyName("compatible_engine_versions")]
    public required string[] CompatibleEngineVersions { get; init; }

    [JsonPropertyName("input_schema_ref")]
    public required string InputSchemaRef { get; init; }

    [JsonPropertyName("subject_types")]
    public required string[] SubjectTypes { get; init; }

    [JsonPropertyName("knowledge_requirements")]
    public required string[] KnowledgeRequirements { get; init; }

    [JsonPropertyName("policy_profile_refs")]
    public required string[] PolicyProfileRefs { get; init; }

    [JsonPropertyName("expected_observation")]
    public required string ExpectedObservation { get; init; }

    [JsonPropertyName("reason_code_namespace")]
    public required string ReasonCodeNamespace { get; init; }

    [JsonPropertyName("evidence_requirements")]
    public required string EvidenceRequirements { get; init; }

    [JsonPropertyName("human_review_template")]
    public required string HumanReviewTemplate { get; init; }

    [JsonPropertyName("golden_cases")]
    public required ScenarioPackGoldenCase[] GoldenCases { get; init; }

    [JsonPropertyName("value_metrics")]
    public required ScenarioPackValueMetric[] ValueMetrics { get; init; }

    [JsonPropertyName("known_limitations")]
    public required string KnownLimitations { get; init; }
}

public sealed record ScenarioPackGoldenCase
{
    [JsonPropertyName("case_id")]
    public required string CaseId { get; init; }

    [JsonPropertyName("input_ref")]
    public required string InputRef { get; init; }

    [JsonPropertyName("expected_conclusion")]
    public required string ExpectedConclusion { get; init; }

    [JsonPropertyName("expected_reason_code")]
    public required string ExpectedReasonCode { get; init; }
}

public sealed record ScenarioPackValueMetric
{
    [JsonPropertyName("metric_id")]
    public required string MetricId { get; init; }

    [JsonPropertyName("definition")]
    public required string Definition { get; init; }

    [JsonPropertyName("collection_point")]
    public required string CollectionPoint { get; init; }
}
