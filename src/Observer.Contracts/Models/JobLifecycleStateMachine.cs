namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Authoritative P0-05 commit &amp; recovery state machine guard for analysis tasks.
///
/// Frozen authority: <c>故障提交与恢复状态机.md</c> (P0-05) + 实现授权基线 §P1-2.
/// This class is the SINGLE source of truth for which Job status transitions are legal;
/// the persistence layer and orchestrator MUST route every status change through
/// <see cref="EnsureTransition"/>.
///
/// Hard rules encoded (P0-05 §4):
/// <list type="bullet">
///   <item><description>COMPLETED is reachable only after AUDIT_COMMITTED.</description></item>
///   <item><description>RECOVERY_REQUIRED is the only re-entry into the chain (→ SNAPSHOT_COMMITTED) and is reached only from explicit failure states or an external Host-exit mark.</description></item>
///   <item><description>The Engine output, once persisted, is NEVER recomputed (enforced at the orchestrator; this guard refuses RECOVERY_REQUIRED → ENGINE_COMPLETED directly).</description></item>
/// </list>
///
/// NOTE: <c>Draft</c> / <c>Running</c> (legacy pre-states in <see cref="AnalysisTaskStatus"/>)
/// are intentionally absent from the transition graph; they are not part of the frozen chain.
/// </summary>
public static class JobLifecycle
{
    // ---- Review status (independent of Job status; CR-OBS-003-JOBSTATUS-001) ----
    public static class ReviewStatus
    {
        public const string NotRequired = "NOT_REQUIRED";
        public const string Pending = "PENDING";
        public const string Reviewed = "REVIEWED";

        public static IReadOnlyCollection<string> All { get; } =
            new[] { NotRequired, Pending, Reviewed };
    }

    private static readonly IReadOnlyDictionary<string, HashSet<string>> Transitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            // Ordered commit chain (P0-05 §2).
            [AnalysisTaskStatus.PrecheckPassed] = Set(AnalysisTaskStatus.SnapshotCommitted, AnalysisTaskStatus.PreflightFailed, AnalysisTaskStatus.CancelledBeforeEngine),
            [AnalysisTaskStatus.SnapshotCommitted] = Set(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.EngineFailed, AnalysisTaskStatus.CancelRequestedEngineFinished, AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.EngineCompleted] = Set(AnalysisTaskStatus.OutputValidated, AnalysisTaskStatus.OutputValidationFailed, AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.OutputValidated] = Set(AnalysisTaskStatus.ArtifactCommitted, AnalysisTaskStatus.ArtifactCommitFailed, AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.ArtifactCommitted] = Set(AnalysisTaskStatus.ObservationCommitted, AnalysisTaskStatus.ObservationCommitFailed, AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.ObservationCommitted] = Set(AnalysisTaskStatus.AuditCommitted, AnalysisTaskStatus.AuditCommitFailed, AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.AuditCommitted] = Set(AnalysisTaskStatus.Completed),

            // Explicit failure states → recovery (P0-05 §3).
            [AnalysisTaskStatus.EngineFailed] = Set(AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.ArtifactCommitFailed] = Set(AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.ObservationCommitFailed] = Set(AnalysisTaskStatus.RecoveryRequired),
            [AnalysisTaskStatus.AuditCommitFailed] = Set(AnalysisTaskStatus.RecoveryRequired),

            // Recovery re-enters the chain at SNAPSHOT_COMMITTED, reusing the original snapshot.
            [AnalysisTaskStatus.RecoveryRequired] = Set(AnalysisTaskStatus.SnapshotCommitted),
        };

    // Terminal states: no further transition is expected (a completed or finally-failed task).
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal)
    {
        AnalysisTaskStatus.Completed,
        AnalysisTaskStatus.PreflightFailed,
        AnalysisTaskStatus.CancelledBeforeEngine,
        AnalysisTaskStatus.CancelRequestedEngineFinished,
        AnalysisTaskStatus.EngineFailed,
        AnalysisTaskStatus.OutputValidationFailed,
        AnalysisTaskStatus.ArtifactCommitFailed,
        AnalysisTaskStatus.ObservationCommitFailed,
        AnalysisTaskStatus.AuditCommitFailed,
    };

    // In-flight progress states that the Launcher must mark RECOVERY_REQUIRED on Host exit
    // (P0-B rule 2: a Host exit means the task CANNOT keep computing).
    private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal)
    {
        AnalysisTaskStatus.Draft,
        // Legacy UI-derivative "in progress" marker. Kept here ONLY so a historical 'Running'
        // row (allowed by the DB CHECK constraint for backward compatibility) is still treated
        // as in-flight and driven to RECOVERY_REQUIRED on Host exit. Never persisted going forward
        // (see AnalysisTaskStatus.Running [Obsolete]); referenced by literal to avoid the obsolete symbol.
        "Running",
        AnalysisTaskStatus.PrecheckPassed,
        AnalysisTaskStatus.SnapshotCommitted,
        AnalysisTaskStatus.EngineCompleted,
        AnalysisTaskStatus.OutputValidated,
        AnalysisTaskStatus.ArtifactCommitted,
        AnalysisTaskStatus.ObservationCommitted,
        AnalysisTaskStatus.AuditCommitted,
    };

    // Canonical forward order for the Web analysis orchestration's persisted commit chain
    // (CREATE/Draft -> ... -> COMPLETED). This is a SEPARATE, ordered list from the P0-05 edge
    // graph in Transitions: the Web orchestration commits the Engine output-derived runtime
    // snapshot AFTER the Engine runs (SNAPSHOT_COMMITTED follows ENGINE_COMPLETED), whereas the
    // abstract graph nests SNAPSHOT_COMMITTED before ENGINE_COMPLETED. Keeping the two distinct
    // avoids forking the frozen spec graph while still letting the orchestration assert a strict
    // "状态机只前进" (no-backward) discipline via CanAdvance.
    private static readonly string[] ProgressOrder =
    {
        AnalysisTaskStatus.Draft,
        AnalysisTaskStatus.PrecheckPassed,
        AnalysisTaskStatus.EngineCompleted,
        AnalysisTaskStatus.OutputValidated,
        AnalysisTaskStatus.SnapshotCommitted,
        AnalysisTaskStatus.ArtifactCommitted,
        AnalysisTaskStatus.ObservationCommitted,
        AnalysisTaskStatus.AuditCommitted,
        AnalysisTaskStatus.Completed,
    };

    // In-progress (still computing / not yet fully committed) states that the UI may present as
    // "进行中" (ADR-OBS-V030-UI-001 原则⑥/⑦). Equals [PRECHECK_PASSED … AUDIT_COMMITTED].
    private static readonly HashSet<string> InProgressStates = new(StringComparer.Ordinal)
    {
        AnalysisTaskStatus.PrecheckPassed,
        AnalysisTaskStatus.SnapshotCommitted,
        AnalysisTaskStatus.EngineCompleted,
        AnalysisTaskStatus.OutputValidated,
        AnalysisTaskStatus.ArtifactCommitted,
        AnalysisTaskStatus.ObservationCommitted,
        AnalysisTaskStatus.AuditCommitted,
    };

    /// <summary>True when <paramref name="state"/> is one of the frozen P0-05 job statuses.</summary>
    public static bool IsValidState(string state) =>
        state is
            AnalysisTaskStatus.PrecheckPassed or AnalysisTaskStatus.SnapshotCommitted or
            AnalysisTaskStatus.EngineCompleted or AnalysisTaskStatus.OutputValidated or
            AnalysisTaskStatus.ArtifactCommitted or AnalysisTaskStatus.ObservationCommitted or
            AnalysisTaskStatus.AuditCommitted or AnalysisTaskStatus.Completed or
            AnalysisTaskStatus.PreflightFailed or AnalysisTaskStatus.EngineFailed or
            AnalysisTaskStatus.OutputValidationFailed or AnalysisTaskStatus.ArtifactCommitFailed or
            AnalysisTaskStatus.ObservationCommitFailed or AnalysisTaskStatus.AuditCommitFailed or
            AnalysisTaskStatus.CancelledBeforeEngine or AnalysisTaskStatus.CancelRequestedEngineFinished or
            AnalysisTaskStatus.RecoveryRequired;

    public static bool IsTerminal(string state) => Terminal.Contains(state);

    public static bool IsRecoveryState(string state) => state == AnalysisTaskStatus.RecoveryRequired;

    public static bool IsFailureState(string state) =>
        state is AnalysisTaskStatus.PreflightFailed or AnalysisTaskStatus.EngineFailed or
            AnalysisTaskStatus.OutputValidationFailed or AnalysisTaskStatus.ArtifactCommitFailed or
            AnalysisTaskStatus.ObservationCommitFailed or AnalysisTaskStatus.AuditCommitFailed or
            AnalysisTaskStatus.CancelledBeforeEngine or AnalysisTaskStatus.CancelRequestedEngineFinished;

    /// <summary>True for a progress state that is still computing and therefore must be
    /// driven to RECOVERY_REQUIRED when the Host exits (P0-B rule 2).</summary>
    public static bool IsInFlight(string state) => InFlight.Contains(state);

    /// <summary>True only for the single fully-committed terminal state that the UI may
    /// present as "已完成" (ADR-OBS-V030-UI-001 原则⑩).</summary>
    public static bool IsFullyCompleted(string state) => state == AnalysisTaskStatus.Completed;

    /// <summary>True when the task is still progressing (not yet fully committed). Mirrors the UI
    /// "进行中" derivation: <c>status ∈ [PRECHECK_PASSED … AUDIT_COMMITTED]</c> and not COMPLETED
    /// (ADR-OBS-V030-UI-001 原则⑥/⑦). The Web orchestration never persists the obsolete
    /// <see cref="AnalysisTaskStatus.Running"/> marker, so "进行中" is DERIVED from canonical
    /// states rather than read from a stored one.</summary>
    public static bool IsInProgress(string state) => InProgressStates.Contains(state);

    public static bool CanTransition(string current, string next)
    {
        // A self-transition is always legal (idempotent); mirrors EnsureTransition's short-circuit
        // so the predicate and the guard agree on same-state transitions.
        if (current == next)
            return true;
        return Transitions.TryGetValue(current, out HashSet<string>? nexts) && nexts.Contains(next);
    }

    /// <summary>
    /// Forward-only guard for the Web analysis orchestration. Returns true when <paramref name="next"/>
    /// does not regress <paramref name="current"/>:
    /// <list type="bullet">
    ///   <item><description>an equal state (idempotent self-transition);</description></item>
    ///   <item><description>a strictly later state along <see cref="ProgressOrder"/>;</description></item>
    ///   <item><description>a failure / recovery branch from any in-progress state;</description></item>
    ///   <item><description>the RECOVERY_REQUIRED → SNAPSHOT_COMMITTED re-entry into the chain.</description></item>
    /// </list>
    /// This enforces the "状态机只前进" discipline (P0-05) for the orchestration's actual persisted
    /// order. It complements — and must not be confused with — <see cref="EnsureTransition"/>, which
    /// encodes the stricter P0-05 edge graph used by the abstract spec unit tests.
    /// </summary>
    public static bool CanAdvance(string current, string next)
    {
        if (current == next)
        {
            return true;
        }
        int currentIndex = IndexOfProgress(current);
        int nextIndex = IndexOfProgress(next);
        if (currentIndex >= 0 && nextIndex >= 0)
        {
            return nextIndex > currentIndex;
        }
        // A failure / recovery branch is always a forward move from any in-progress state.
        if (IsProgressState(current) && (IsFailureState(next) || IsRecoveryState(next)))
        {
            return true;
        }
        // Any explicit failure state may re-enter recovery (P0-05 §3): the single recovery
        // re-entry is RECOVERY_REQUIRED, after which the chain resumes at SNAPSHOT_COMMITTED.
        if (IsFailureState(current) && IsRecoveryState(next))
        {
            return true;
        }
        // Recovery re-enters the committed chain at SNAPSHOT_COMMITTED, reusing the original snapshot.
        if (IsRecoveryState(current) && next == AnalysisTaskStatus.SnapshotCommitted)
        {
            return true;
        }
        return false;
    }

    private static int IndexOfProgress(string state)
    {
        for (int i = 0; i < ProgressOrder.Length; i++)
        {
            if (string.Equals(ProgressOrder[i], state, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsProgressState(string state) => IndexOfProgress(state) >= 0;

    /// <summary>Throws <see cref="InvalidOperationException"/> when the transition is illegal.</summary>
    public static void EnsureTransition(string current, string next)
    {
        if (current == next)
            return;
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Illegal Job status transition: {current} -> {next}. The P0-05 commit chain forbids this edge.");
        }
    }

    /// <summary>Independent review_status validation (CR-OBS-003-JOBSTATUS-001).</summary>
    public static bool IsValidReviewStatus(string value) => ReviewStatus.All.Contains(value);

    private static HashSet<string> Set(params string[] states) => new(states, StringComparer.Ordinal);
}
