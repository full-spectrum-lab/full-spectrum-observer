using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// Parses the Engine v1.5 response envelope into Observer domain objects. PURE pass-through:
/// unknown_state / hard_gate / conflicts / conclusion are stored verbatim — never recomputed,
/// merged, or downgraded (red lines #6 / #9). No governance logic lives here.
/// </summary>
public sealed class OutputAdapter
{
    /// <summary>Splits a validated Engine response into Observer result/conflict/snapshot/evidence.</summary>
    public AnalysisOutput Parse(EngineResponse response, AnalysisTask task, string createdAtUtc)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        string resultId = $"RES-{task.TaskId}";
        string conclusionJson = response.Conclusion is { } conclusion
            ? conclusion.GetRawText()
            : "{}";

        var result = new AnalysisResult
        {
            ResultId = resultId,
            TaskId = task.TaskId,
            ConclusionPayload = conclusionJson,
            UnknownState = response.UnknownState, // verbatim
            HardGate = response.HardGate,          // verbatim
            CreatedAt = createdAtUtc,
        };

        var conflicts = (response.ConflictObservations ?? new List<EngineConflictObservation>())
            .Select((c, index) => new ConflictObservation
            {
                ObservationId = $"OBS-{task.TaskId}-{(index + 1):D2}",
                ResultId = resultId,
                ConflictType = c.ConflictType,
                InvolvedSubjects = c.InvolvedSubjects.ToImmutableArray(),
                Severity = c.Severity,
                HumanReviewRequired = c.HumanReviewRequired,
                ReasonCode = c.ReasonCode,
                MissingContext = c.MissingContext is { } missing ? missing.ToImmutableArray() : null,
            })
            .ToList();

        var snapshot = new RuntimeSnapshot
        {
            SnapshotId = $"SNP-{task.TaskId}",
            ResultId = resultId,
            AnalyzerVersion = response.AnalyzerVersion,
            EngineVersion = response.EngineVersion, // pinned 1.5.0 (DB CHECK)
            ProfileVersion = response.ProfileVersion,
            SchemaVersion = response.SchemaVersion,
            InputDigest = task.ContentDigest, // replay anchor (must equal content_digest)
            ConfigDigest = response.SchemaDigest,
            RuntimeDigest = response.RuntimeDigest,
        };

        var evidence = new EvidenceBundle
        {
            BundleId = $"EVID-{task.TaskId}",
            ResultId = resultId,
            EvidenceDigest = response.Evidence?.EvidenceDigest ?? string.Empty, // verbatim
            References = (response.Evidence?.References ?? new List<string>()).ToImmutableArray(),
        };

        return new AnalysisOutput(result, conflicts, snapshot, evidence);
    }
}

/// <summary>Split output of a single Engine analysis pass.</summary>
/// <param name="Result">The analysis result (conclusion verbatim).</param>
/// <param name="Conflicts">Conflict observations (pass-through).</param>
/// <param name="Snapshot">Runtime snapshot (replay anchor).</param>
/// <param name="Evidence">Evidence bundle (digest verbatim).</param>
public sealed record AnalysisOutput(
    AnalysisResult Result,
    List<ConflictObservation> Conflicts,
    RuntimeSnapshot Snapshot,
    EvidenceBundle Evidence);
