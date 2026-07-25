using System.Text.Json;
using System.Text.Json.Nodes;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// DET-001-FIX — unified, fail-closed normalizer that guarantees every scenario sent to the
/// pinned Engine v1.5.0 worker carries a deterministic <c>simulation_id</c>.
///
/// Frozen rule (OWNER authorization DET-001-FIX):
///   - scenario already contains a non-empty simulation_id -&gt; keep it verbatim (never overwrite).
///   - scenario lacks simulation_id                          -&gt; derive "SIM-" + first 16 hex chars
///                                                                of the request content digest (lowercase).
///   - content digest invalid / empty / &lt; 16 hex chars      -&gt; fail BEFORE the worker is spawned
///                                                                (never fall back to time / GUID / random /
///                                                                 process id / machine name / task creation time).
///
    /// This method is the single chokepoint for Web FORM, CLI and case-pack intake:
    ///   - Web path:  invoked by <see cref="EngineFacade.AnalyzeAsync"/> immediately before the worker
    ///                request envelope is built.
    ///   - CLI path:  invoked by FoundationAnalysisUseCase (Observer.Execution) immediately before the
    ///                EngineFacadeRequest.Scenario is assigned.
    /// so no other call site can omit the field again.
/// </summary>
public static class EnvelopeNormalizer
{
    /// <summary>Reason code when the resolved simulation_id cannot be honored.</summary>
    public const string ReasonCodeInvalidSimulationId = "INVALID_SIMULATION_ID_CONTRACT";

    /// <summary>Reason code when the content digest is missing / too short / non-hex.</summary>
    public const string ReasonCodeInvalidContentDigest = "INVALID_CONTENT_DIGEST_CONTRACT";

    /// <summary>Number of leading hex characters of the content digest used to derive simulation_id.</summary>
    public const int RequiredHexLength = 16;

    /// <summary>
    /// Returns the normalized scenario (guaranteed to carry a simulation_id) together with the
    /// resolved simulation_id value that will be sent to the Engine worker.
    /// </summary>
    /// <exception cref="ContractViolationException">
    /// Thrown when the content digest is missing / too short / non-hex, or the scenario is not a JSON object.
    /// </exception>
    public static (JsonElement NormalizedScenario, string ResolvedSimulationId) EnsureDeterministicSimulationId(
        JsonElement scenario,
        string contentDigest)
    {
        if (string.IsNullOrWhiteSpace(contentDigest) || contentDigest.Length < RequiredHexLength)
        {
            throw new ContractViolationException(
                ReasonCodeInvalidContentDigest,
                $"[{ReasonCodeInvalidContentDigest}] Content digest must be at least {RequiredHexLength} hex characters to derive a deterministic simulation_id; got: {contentDigest ?? "null"}.");
        }

        // The derived simulation_id is taken verbatim from the digest prefix, so the digest MUST be
        // pure hexadecimal — otherwise the derived id would not be stable / canonical.
        for (int i = 0; i < RequiredHexLength; i++)
        {
            char c = contentDigest[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                throw new ContractViolationException(
                    ReasonCodeInvalidContentDigest,
                    $"[{ReasonCodeInvalidContentDigest}] Content digest must be hexadecimal; invalid character '{c}' at index {i}.");
            }
        }

        string? existing = TryGetSimulationId(scenario);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            // Preserve the caller-supplied value verbatim (case-pack / explicit CLI paths).
            return (scenario, existing);
        }

        string derived = "SIM-" + contentDigest.Substring(0, RequiredHexLength).ToLowerInvariant();
        JsonElement normalized = SetSimulationId(scenario, derived);
        return (normalized, derived);
    }

    private static string? TryGetSimulationId(JsonElement scenario)
    {
        if (scenario.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (scenario.TryGetProperty("simulation_id", out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static JsonElement SetSimulationId(JsonElement scenario, string simulationId)
    {
        // Re-parse defensively so we never alias the caller's JsonElement; attach/replace
        // simulation_id as a top-level field, preserving every other property verbatim.
        JsonNode? node = JsonNode.Parse(scenario.GetRawText());
        var obj = node as JsonObject ?? new JsonObject { ["_payload"] = node?.DeepClone() };
        obj["simulation_id"] = simulationId;
        return JsonSerializer.SerializeToElement(obj);
    }
}
