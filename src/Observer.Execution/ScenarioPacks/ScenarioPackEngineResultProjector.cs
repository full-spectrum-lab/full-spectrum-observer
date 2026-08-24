using System.Collections.Immutable;
using System.Text;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed record ScenarioPackExecutionProjection(
    AnalysisResult Result,
    RuntimeSnapshot Snapshot,
    EvidenceBundle? Evidence,
    IReadOnlyList<ConflictObservation> Observations);

public static class ScenarioPackEngineResultProjector
{
    public static ScenarioPackExecutionProjection Project(
        string taskId,
        string resultId,
        ScenarioPackEngineRequest request,
        EngineResponse response,
        string createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (response.HardGate)
        {
            throw new InvalidDataException(
                "SCENARIO_PACK_AUTOMATIC_ACTION_FORBIDDEN: v0.4 accepts candidate observations only.");
        }
        if (!string.Equals(response.EngineVersion, request.Request.EngineVersion, StringComparison.Ordinal) ||
            !string.Equals(response.ProfileVersion, request.Request.ProfileVersion, StringComparison.Ordinal) ||
            !string.Equals(response.SchemaVersion, request.Request.SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SCENARIO_PACK_RUNTIME_IDENTITY_MISMATCH: response cannot be replay-bound to the request.");
        }

        var result = new AnalysisResult
        {
            ResultId = resultId,
            TaskId = taskId,
            ConclusionPayload = response.Conclusion?.GetRawText() ?? "null",
            UnknownState = response.UnknownState,
            HardGate = false,
            CreatedAt = createdAtUtc,
        };
        string profileBinding = ScenarioPackEngineRequestFactory.CreateProfileBindingJson(request);
        var snapshot = new RuntimeSnapshot
        {
            SnapshotId = "SNP-" + resultId,
            ResultId = resultId,
            AnalyzerVersion = response.AnalyzerVersion,
            EngineVersion = response.EngineVersion,
            ProfileVersion = response.ProfileVersion,
            SchemaVersion = response.SchemaVersion,
            InputDigest = request.InputDigest,
            ConfigDigest = FsObsCanonicalizer.Sha256Hex(Encoding.UTF8.GetBytes(profileBinding)),
            RuntimeDigest = response.RuntimeDigest,
            ResolvedSimulationId = response.ResolvedSimulationId,
        };
        EvidenceBundle? evidence = response.Evidence is null ? null : new EvidenceBundle
        {
            BundleId = "EVID-" + resultId,
            ResultId = resultId,
            EvidenceDigest = response.Evidence.EvidenceDigest,
            References = response.Evidence.References.ToImmutableArray(),
            ResolvedSimulationId = response.ResolvedSimulationId,
        };
        IReadOnlyList<ConflictObservation> observations = (response.ConflictObservations ?? [])
            .Select((observation, index) => new ConflictObservation
            {
                ObservationId = $"OBS-{resultId}-{index + 1:D3}",
                ResultId = resultId,
                ConflictType = observation.ConflictType,
                InvolvedSubjects = observation.InvolvedSubjects.ToImmutableArray(),
                Severity = observation.Severity,
                HumanReviewRequired = observation.HumanReviewRequired,
                ReasonCode = observation.ReasonCode,
                MissingContext = observation.MissingContext?.ToImmutableArray(),
                ReviewFlag = "PENDING",
                ReviewNote = null,
            })
            .ToArray();
        return new ScenarioPackExecutionProjection(result, snapshot, evidence, observations);
    }
}
