using System.Collections.Immutable;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Recovery;

/// <summary>
/// Builds a <see cref="RecoveryPlan"/> for a task that entered <c>RECOVERY_REQUIRED</c>.
///
/// Frozen discipline applied (P0-05 §3 / P0-B rule 3):
/// <list type="bullet">
///   <item><description>Interrupted BEFORE the Engine persisted output (no stored snapshot) →
///     <see cref="RecoveryStrategy.ReRunFromSnapshot"/>: re-run the pinned Engine with the original
///     <see cref="RuntimeSnapshot"/> / version bindings. The current default configuration is NEVER used.</description></item>
///   <item><description>Interrupted AFTER the Engine completed (snapshot present) →
///     <see cref="RecoveryStrategy.ResumePostEngine"/>: continue from the first uncommitted phase;
///     the Engine output is immutable and must not be recomputed.</description></item>
/// </list>
/// </summary>
public static class RecoveryPlanner
{
    /// <summary>
    /// Builds the recovery plan. Throws when the task is not in <c>RECOVERY_REQUIRED</c> (the only
    /// state from which a rebuild may begin).
    /// </summary>
    public static RecoveryPlan Build(AnalysisTask task, RuntimeSnapshot? snapshot)
    {
        if (task.Status != AnalysisTaskStatus.RecoveryRequired)
        {
            throw new InvalidOperationException(
                $"Recovery may only be planned for RECOVERY_REQUIRED tasks; task {task.TaskId} is {task.Status}.");
        }

        // Copy the original, locked bindings verbatim. These values are the source of truth for the
        // rebuild and must not be overwritten by any "current default" configuration (P0-05 规则⑤).
        ImmutableArray<string> knowledgeVersionIds = task.KnowledgeVersionIds;
        string canonicalInput = task.CanonicalInput;
        string contentDigest = task.ContentDigest;

        if (snapshot is null)
        {
            // Engine had not persisted a result; the pinned Engine must re-run from the original task.
            return new RecoveryPlan(
                TaskId: task.TaskId,
                Strategy: RecoveryStrategy.ReRunFromSnapshot,
                EngineRerunRequired: true,
                ResumeFromPhase: RecoveryResumePhase.SnapshotCommitted,
                SubjectVersionId: task.SubjectVersionId,
                KnowledgeVersionIds: knowledgeVersionIds,
                CanonicalInput: canonicalInput,
                ContentDigest: contentDigest,
                Snapshot: null);
        }

        // Engine completed and the snapshot is immutable; resume committing downstream phases.
        return new RecoveryPlan(
            TaskId: task.TaskId,
            Strategy: RecoveryStrategy.ResumePostEngine,
            EngineRerunRequired: false,
            ResumeFromPhase: RecoveryResumePhase.ArtifactCommitted,
            SubjectVersionId: task.SubjectVersionId,
            KnowledgeVersionIds: knowledgeVersionIds,
            CanonicalInput: canonicalInput,
            ContentDigest: contentDigest,
            Snapshot: snapshot);
    }
}
