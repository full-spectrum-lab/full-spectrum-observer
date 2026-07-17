using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Maps a persisted Job status (+ independent review_status) to a UI-derivative display value,
/// strictly per the UI 状态映射矩阵 (P0-05 + 实现授权基线 §P1-2) and ADR-OBS-V030-UI-001 原则⑩.
///
/// INVARIANTS enforced here (never violated by the backend):
/// <list type="bullet">
///   <item><description>Only <c>COMPLETED</c> (after AUDIT_COMMITTED) may be presented as "已完成".</description></item>
///   <item><description><c>ENGINE_COMPLETED</c> is presented as "Engine 完成（未完整）" — NEVER as the full task completion.</description></item>
///   <item><description>Recovery / failure states are shown explicitly; the UI never synthesizes a success.</description></item>
///   <item><description>The display is DERIVED only; it is never written back to the Job status (Circuit 原则⑥/⑦).</description></item>
/// </list>
/// </summary>
public sealed class JobStatusPresenter
{
    public sealed record Display(
        string JobStatus,
        string Label,
        string Tone,
        bool IsFullyCompleted,
        bool RequiresRecovery,
        bool IsFailure,
        string? Hint);

    public Display Present(string jobStatus, string reviewStatus = "NOT_REQUIRED")
    {
        (string label, string tone, bool complete, bool recovery, bool failure, string? hint) = jobStatus switch
        {
            AnalysisTaskStatus.Draft => ("草稿", "muted", false, false, false, null),
            // Historical UI-derivative "in progress" marker. Referenced by literal (never the
            // obsolete AnalysisTaskStatus.Running symbol) so the enum member stays unused going
            // forward; new tasks never persist this value.
            "Running" => ("执行中(历史)", "info", false, false, false, "历史 UI 派生状态；新任务不再持久化 Running。"),
            AnalysisTaskStatus.PrecheckPassed => ("预检查通过", "info", false, false, false, null),
            AnalysisTaskStatus.SnapshotCommitted => ("快照已提交", "info", false, false, false, null),
            AnalysisTaskStatus.EngineCompleted => ("Engine 完成（未完整）", "warn", false, false, false, "Engine 已完成，尚未完成落库，非任务完成。"),
            AnalysisTaskStatus.OutputValidated => ("输出已校验", "info", false, false, false, null),
            AnalysisTaskStatus.ArtifactCommitted => ("证据已提交", "info", false, false, false, null),
            AnalysisTaskStatus.ObservationCommitted => ("观察已提交", "info", false, false, false, null),
            AnalysisTaskStatus.AuditCommitted => ("审计已提交", "info", false, false, false, null),
            AnalysisTaskStatus.Completed => ("已完成", "success", true, false, false, null),
            AnalysisTaskStatus.PreflightFailed => ("预检查失败", "danger", false, false, true, null),
            AnalysisTaskStatus.EngineFailed => ("Engine 失败", "danger", false, false, true, "依赖缺失/不可重放，阻断。"),
            AnalysisTaskStatus.OutputValidationFailed => ("输出校验失败", "danger", false, false, true, "版本/digest 校验失败，阻断持久化。"),
            AnalysisTaskStatus.ArtifactCommitFailed => ("证据提交失败", "danger", false, true, true, "需恢复。"),
            AnalysisTaskStatus.ObservationCommitFailed => ("观察提交失败", "danger", false, true, true, "需恢复。"),
            AnalysisTaskStatus.AuditCommitFailed => ("审计提交失败", "danger", false, true, true, "审计未完整，不得显示“完整完成”。"),
            AnalysisTaskStatus.CancelledBeforeEngine => ("Engine 前已取消", "muted", false, false, true, null),
            AnalysisTaskStatus.CancelRequestedEngineFinished => ("取消请求（Engine 已结束）", "muted", false, false, true, "以 Engine 真实结果为准。"),
            AnalysisTaskStatus.RecoveryRequired => ("需恢复", "warn", false, true, false, "将按原 Runtime Snapshot 恢复续跑。"),
            _ => ("未知状态", "danger", false, false, true, null),
        };

        return new Display(jobStatus, label, tone, complete, recovery, failure, hint);
    }

    /// <summary>The single gate for the "已完成" badge (原则⑩): true ONLY for COMPLETED.</summary>
    public bool IsFullyCompleted(string jobStatus) => JobLifecycle.IsFullyCompleted(jobStatus);

    /// <summary>True when the task is still progressing (derived from canonical states, never from
    /// the obsolete <c>Running</c> marker). Mirrors <see cref="JobLifecycle.IsInProgress"/>.</summary>
    public bool IsInProgress(string jobStatus) => JobLifecycle.IsInProgress(jobStatus);

    /// <summary>True when the task needs recovery (RECOVERY_REQUIRED).</summary>
    public bool RequiresRecovery(string jobStatus) => JobLifecycle.IsRecoveryState(jobStatus);

    /// <summary>Independent review_status display (CR-OBS-003-JOBSTATUS-001). Never alters the
    /// Engine execution fact.</summary>
    public static string PresentReviewStatus(string reviewStatus) => reviewStatus switch
    {
        JobLifecycle.ReviewStatus.Pending => "待复核",
        JobLifecycle.ReviewStatus.Reviewed => "已复核",
        _ => "无需复核",
    };
}
