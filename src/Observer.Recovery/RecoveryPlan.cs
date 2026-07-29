using System.Collections.Immutable;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Recovery;

/// <summary>How a recovered attempt must be rebuilt (P0-05 §3 / P0-B rule 3).</summary>
public enum RecoveryStrategy
{
    /// <summary>Engine had not persisted output; re-run the pinned Engine using the ORIGINAL
    /// runtime snapshot + version bindings (never the current default configuration).</summary>
    ReRunFromSnapshot,

    /// <summary>Engine already completed and its output is immutable; resume committing the
    /// downstream phases (Artifact / Observation / Audit) without re-running the Engine.</summary>
    ResumePostEngine,
}

/// <summary>The first committed-chain phase a recovered attempt must reach.</summary>
public enum RecoveryResumePhase
{
    /// <summary>Resume committing the result + snapshot (Engine will re-run).</summary>
    SnapshotCommitted,

    /// <summary>Engine output is present; resume at Artifact commit.</summary>
    ArtifactCommitted,

    /// <summary>No resume needed (terminal / unexpected).</summary>
    None,
}

/// <summary>
/// A rebuild plan for a single <c>RECOVERY_REQUIRED</c> task. The plan pins the ORIGINAL
/// subject/knowledge version bindings, canonical input and content digest — these are copied
/// verbatim from the stored task and MUST NOT be replaced by "current default" values
/// (P0-05 规则⑤ / ADR-001). The Engine output is never recomputed when
/// <see cref="EngineRerunRequired"/> is false.
/// </summary>
public sealed record RecoveryPlan(
    string TaskId,
    RecoveryStrategy Strategy,
    bool EngineRerunRequired,
    RecoveryResumePhase ResumeFromPhase,
    string SubjectVersionId,
    ImmutableArray<string> KnowledgeVersionIds,
    string CanonicalInput,
    string ContentDigest,
    RuntimeSnapshot? Snapshot)
{
    /// <summary>True when the original runtime snapshot is available to replay (Engine had completed).</summary>
    public bool HasSnapshot => Snapshot is not null;
}
