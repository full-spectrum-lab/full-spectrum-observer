using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// The raw analysis input envelope. All three intake modes (FORM / JSON_IMPORT /
/// SANITIZED_FILE) normalize to this single structure so the Engine sees no mode branch.
/// </summary>
public sealed record RawAnalysisInput
{
    /// <summary>Intake mode: <c>FORM</c> / <c>JSON_IMPORT</c> / <c>SANITIZED_FILE</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Canonical input JSON: {user_question, ai_output, context}.</summary>
    public required string CanonicalInput { get; init; }

    /// <summary>sha256 of <see cref="CanonicalInput"/>; the input fingerprint (red line #8).</summary>
    public required string ContentDigest { get; init; }

    /// <summary>Desensitization / transformation trace JSON array; default <c>[]</c>.</summary>
    public string? TransformTrace { get; init; }

    /// <summary>Returns the <c>input</c> sub-object for the Engine v1.5 request envelope.</summary>
    public EngineInputShape ToEngineInput() => new(Mode, CanonicalInput, ContentDigest, TransformTrace ?? "[]");
}

/// <summary>
/// Wire shape of the Engine request <c>input</c> object. Mirrors <see cref="RawAnalysisInput"/>.
/// </summary>
public sealed record EngineInputShape(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("canonical_input")] JsonElement CanonicalInput,
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("transform_trace")] JsonElement TransformTrace)
{
    public EngineInputShape(string mode, string canonicalInputJson, string contentDigest, string transformTraceJson)
        : this(mode, JsonSerializer.Deserialize<JsonElement>(canonicalInputJson), contentDigest, JsonSerializer.Deserialize<JsonElement>(transformTraceJson))
    {
    }
}
