using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Execution;

public sealed class FoundationValidationPipeline
{
    private readonly FoundationExecutionOptions _options;
    private readonly IClock _clock;

    public FoundationValidationPipeline(FoundationExecutionOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public ValidationResultContract ValidateRequest(FoundationAnalysisRequest request)
    {
        byte[] bytes = FoundationJson.Serialize(request);
        using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(_options.SchemaDirectory);
        SchemaValidationReport report = bundle.Validate("foundation-analysis-request", bytes);
        return ToContract("SCHEMA", report.IsValid ? "PASS" : "FAIL",
            report.Issues.Select(static issue => new ReasonItem(
                issue.ReasonCode, issue.Message, issue.JsonPath, false, true)).ToArray(),
            [], report.Issues.Select(static issue => issue.JsonPath).ToArray());
    }

    public (ValidationResultContract Contract, GovernanceValidationSummary Summary) ValidateGovernance(JsonElement context)
    {
        string[] required =
        [
            "simulation_id", "input_query", "sensitivity_level", "initial_state", "agents",
            "weights", "ess_horizon", "ess_candidates", "conflict_density", "reversibility", "diffusivity"
        ];
        string[] unknown = required.Where(name => !context.TryGetProperty(name, out JsonElement value) || IsEmpty(value)).ToArray();
        var reasons = new List<ReasonItem>();
        if (unknown.Length > 0)
        {
            reasons.Add(new ReasonItem(FoundationReasonCodes.GOV_CONTEXT_INSUFFICIENT,
                "Governance context is incomplete.", "$", false, true));
            reasons.Add(new ReasonItem(FoundationReasonCodes.GOV_UNKNOWN_DECLARED,
                "Unknown governance fields were declared.", "$", false, true));
            reasons.Add(new ReasonItem(FoundationReasonCodes.GOV_HUMAN_REVIEW_REQUIRED,
                "Human review is required before formal observation.", "$", false, true));
        }
        string status = unknown.Length == 0 ? "PASS" : "NEEDS_EVIDENCE";
        ValidationResultContract contract = ToContract(
            "GOVERNANCE", status, reasons,
            required.Except(unknown, StringComparer.Ordinal).ToArray(), unknown);
        return (contract, new GovernanceValidationSummary(
            unknown.Length == 0,
            unknown,
            reasons.Select(static item => item.Code).ToArray()));
    }

    private ValidationResultContract ToContract(
        string kind,
        string status,
        IEnumerable<ReasonItem> reasons,
        IEnumerable<string> known,
        IEnumerable<string> unknown)
    {
        return new ValidationResultContract
        {
            Contract = "fs-observer/validation-result/1",
            ValidationKind = kind,
            Status = status,
            SchemaRef = null,
            ReasonCodes = JsonSerializer.SerializeToElement(reasons, FoundationJson.CreateOptions()),
            KnownFields = JsonSerializer.SerializeToElement(known),
            UnknownFields = JsonSerializer.SerializeToElement(unknown),
            ValidatedAtUtc = _clock.UtcNow.ToString("O"),
        };
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        _ => false,
    };

    private sealed record ReasonItem(
        string Code,
        string Message,
        string? FieldPath,
        bool Retryable,
        bool DetailsRedacted);
}
