using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

#nullable enable

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Orchestrates a single analysis run: build the request envelope, preflight structure
/// validation, invoke the pinned Engine v1.5.0, then persist the result along the R1-D ordered
/// commit chain. On any contract / dependency failure it writes a WARNING audit and BLOCKS
/// persistence (fail-closed); it never downgrades or recomputes the Engine's conclusion.
///
/// Lifecycle authority (P1 main line): every Job status change is (a) validated as forward-only by
/// <see cref="JobLifecycle.CanAdvance"/>, (b) persisted to SQLite keyed by JobId, and (c) recorded
/// as a chained audit event. The persisted SQLite status is the SINGLE source of truth — the UI
/// only subscribes to / displays it, and a browser refresh or Circuit reconnect re-reads it from
/// the backend. Idempotency is enforced by JobId + request fingerprint (see
/// <see cref="CreateAndRunAsync"/>), so a repeated submission of the same JobId produces no new
/// side effect.
/// </summary>
public sealed class AnalysisWorkspace
{
    private readonly ObserverStore _store;
    private readonly FullSpectrum.Observer.EngineFacade.EngineFacade _engine;
    private readonly IntakeAdapter _intake;
    private readonly OutputAdapter _output;
    private readonly AuditViewer _audit;
    private readonly SubjectCatalog _subjects;
    private readonly KnowledgeCatalog _knowledge;
    private readonly AnalysisShutdownToken _shutdown;

    public AnalysisWorkspace(
        ObserverStore store,
        FullSpectrum.Observer.EngineFacade.EngineFacade engine,
        IntakeAdapter intake,
        OutputAdapter output,
        AuditViewer audit,
        SubjectCatalog subjects,
        KnowledgeCatalog knowledge,
        AnalysisShutdownToken shutdown)
    {
        _store = store;
        _engine = engine;
        _intake = intake;
        _output = output;
        _audit = audit;
        _subjects = subjects;
        _knowledge = knowledge;
        _shutdown = shutdown;
    }

    /// <summary>
    /// Creates a Draft analysis task with an auto-generated JobId and immediately runs it. The
    /// returned outcome's status is authoritative (re-read from SQLite). Prefer
    /// <see cref="CreateAndRunAsync"/> when the caller can supply a stable JobId for idempotency.
    /// </summary>
    public Task<AnalysisRunOutcome> CreateAndRunAsync(
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention) =>
        CreateAndRunAsync(null, subjectVersionId, knowledgeVersionIds, input, retention);

    /// <summary>
    /// Idempotent create-and-run keyed by JobId + request fingerprint.
    /// <list type="bullet">
    ///   <item><description>If <paramref name="requestedJobId"/> is null, a fresh JobId is minted.</description></item>
    ///   <item><description>If a task already exists for the JobId with a MATCHING fingerprint, the
    ///     existing (authoritative) task is returned — no new insert, no new run.</description></item>
    ///   <item><description>If a task exists but the fingerprint differs, the duplicate is rejected.</description></item>
    ///   <item><description>Otherwise the Draft task is created (JobId primary key) and the run proceeds.</description></item>
    /// </list>
    /// </summary>
    public async Task<AnalysisRunOutcome> CreateAndRunAsync(
        string? requestedJobId,
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention)
    {
        string jobId = requestedJobId ?? Ids.Next("TASK");
        AnalysisTask? existing = await _store.GetAnalysisTaskAsync(jobId);
        JobIdempotency.Outcome decision = JobIdempotency.Decide(existing?.ContentDigest, input.ContentDigest);
        if (decision == JobIdempotency.Outcome.Hit)
        {
            // Idempotent repeat: return the authoritative existing state; no new side effect.
            AnalysisTask authoritative = (await _store.GetAnalysisTaskAsync(jobId))!;
            await _audit.AppendAsync("IDEMPOTENT_HIT", jobId, "same JobId + fingerprint; returning existing task");
            return AnalysisRunOutcome.Success(authoritative, null);
        }
        if (decision == JobIdempotency.Outcome.Conflict)
        {
            await _audit.AppendAsync("IDEMPOTENT_CONFLICT", jobId, "same JobId but differing fingerprint; rejected");
            return AnalysisRunOutcome.Failed(jobId, "幂等冲突：相同 JobId 但请求指纹（content digest）不一致，已拒绝重复提交。");
        }
        await CreateDraftTaskAsync(jobId, subjectVersionId, knowledgeVersionIds, input, retention);
        return await RunAnalysisAsync(jobId);
    }

    /// <summary>Creates a Draft analysis task with an auto-generated JobId (backward-compatible).</summary>
    public Task<AnalysisTask> CreateDraftTaskAsync(
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention) =>
        CreateDraftTaskAsync(Ids.Next("TASK"), subjectVersionId, knowledgeVersionIds, input, retention);

    /// <summary>
    /// Creates a Draft analysis task with an EXPLICIT JobId. The JobId is the <c>analysis_tasks</c>
    /// primary key, so a duplicate JobId is rejected by SQLite (the idempotency layer in
    /// <see cref="CreateAndRunAsync"/> short-circuits before reaching this insert).
    /// </summary>
    public async Task<AnalysisTask> CreateDraftTaskAsync(
        string taskId,
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention)
    {
        var task = AnalysisTask.Create(
            taskId,
            subjectVersionId,
            knowledgeVersionIds.ToImmutableArray(),
            input,
            retention.ToWire(),
            SystemClock.UtcNow);
        await _store.InsertAnalysisTaskAsync(task);
        await _audit.AppendAsync("CREATE_TASK", task.TaskId);
        return task;
    }

    public async Task<AnalysisRunOutcome> RunAnalysisAsync(string taskId)
    {
        AnalysisTask? task = await _store.GetAnalysisTaskAsync(taskId);
        if (task is null)
        {
            return AnalysisRunOutcome.Failed(taskId, "任务不存在。");
        }

        SubjectVersion? subjectVersion = await _subjects.GetVersionAsync(task.SubjectVersionId);
        if (subjectVersion is null)
        {
            await TransitionAsync(taskId, AnalysisTaskStatus.PreflightFailed, "PREFLIGHT_FAILED");
            return await FailedReloaded(taskId, "主体版本缺失（预检查失败）。");
        }
        ObservedSubject? subject = await _subjects.GetSubjectAsync(subjectVersion.SubjectId);
        if (subject is null)
        {
            await TransitionAsync(taskId, AnalysisTaskStatus.PreflightFailed, "PREFLIGHT_FAILED");
            return await FailedReloaded(taskId, "主体缺失（预检查失败）。");
        }

        var knowledgeVersions = new List<KnowledgeSourceVersion>();
        foreach (string kvId in task.KnowledgeVersionIds)
        {
            KnowledgeSourceVersion? kv = await _knowledge.GetVersionAsync(kvId);
            if (kv is not null)
            {
                knowledgeVersions.Add(kv);
            }
        }

        var rawInput = new RawAnalysisInput
        {
            Mode = task.InputMode,
            CanonicalInput = task.CanonicalInput,
            ContentDigest = task.ContentDigest,
            TransformTrace = task.TransformTrace,
        };

        EngineRequest envelope = _intake.BuildEnvelope(new BuildEnvelopeRequest(
            "CASE-OBSERVER", subject, subjectVersion, knowledgeVersions, rawInput, RetentionModeExtensions.FromWire(task.RetentionMode)));

        try
        {
            _intake.ValidateSchema(envelope);
        }
        catch (IntakeValidationException exception)
        {
            await TransitionAsync(taskId, AnalysisTaskStatus.PreflightFailed, "PREFLIGHT_FAILED");
            return await FailedReloaded(taskId, "预检查失败：" + exception.Message);
        }
        await TransitionAsync(taskId, AnalysisTaskStatus.PrecheckPassed, "PRECHECK_PASSED");

        EngineResponse response;
        try
        {
            // M2-FIX-03 (T12): observe the shutdown token so a graceful stop cancels this analysis
            // and terminates the Engine worker via the existing EngineFacade cancel path.
            response = await _engine.AnalyzeAsync(envelope, _shutdown.Token);
        }
        catch (DependencyMissingException exception)
        {
            // TC-NEW-003: Engine missing -> "依赖缺失/不可重放", block the task.
            await _audit.AppendAsync("ENGINE_DEPENDENCY_MISSING", taskId, exception.Message);
            await TransitionAsync(taskId, AnalysisTaskStatus.EngineFailed, "ENGINE_FAILED");
            return await FailedReloaded(taskId, "依赖缺失/不可重放：" + exception.Message);
        }
        catch (VersionBindingException exception)
        {
            await _audit.AppendAsync("VERSION_BINDING_FAILED", taskId, exception.Message);
            await TransitionAsync(taskId, AnalysisTaskStatus.OutputValidationFailed, "OUTPUT_VALIDATION_FAILED");
            return await FailedReloaded(taskId, exception.Message);
        }
        catch (ContractViolationException exception)
        {
            // R1-B §5.2: response contract violated -> write WARNING audit, block persistence.
            await _audit.AppendAsync("CONTRACT_VIOLATION", taskId, exception.Message);
            await TransitionAsync(taskId, AnalysisTaskStatus.OutputValidationFailed, "OUTPUT_VALIDATION_FAILED");
            return await FailedReloaded(taskId, "契约违约，阻断持久化：" + exception.Message);
        }

        await TransitionAsync(taskId, AnalysisTaskStatus.EngineCompleted, "ENGINE_COMPLETED");
        await TransitionAsync(taskId, AnalysisTaskStatus.OutputValidated, "OUTPUT_VALIDATED");

        AnalysisOutput output = _output.Parse(response, task, SystemClock.UtcNow);

        // R1-D ordered commit chain. Any failure leaves prior partial artifacts and enters RECOVERY_REQUIRED.
        try
        {
            await _store.InsertAnalysisResultAsync(output.Result);
            await _store.InsertRuntimeSnapshotAsync(output.Snapshot);
            await TransitionAsync(taskId, AnalysisTaskStatus.SnapshotCommitted, "SNAPSHOT_COMMITTED");

            await _store.InsertEvidenceBundleAsync(output.Evidence);
            await TransitionAsync(taskId, AnalysisTaskStatus.ArtifactCommitted, "ARTIFACT_COMMITTED");

            await _store.InsertConflictObservationsAsync(output.Conflicts);
            await TransitionAsync(taskId, AnalysisTaskStatus.ObservationCommitted, "OBSERVATION_COMMITTED");

            await _audit.AppendAsync("RESULT", taskId, $"replay_digest={response.ReplayRef?.Digest}");
            await TransitionAsync(taskId, AnalysisTaskStatus.AuditCommitted, "AUDIT_COMMITTED");

            await TransitionAsync(taskId, AnalysisTaskStatus.Completed, "COMPLETED");
        }
        catch (Exception exception)
        {
            await _audit.AppendAsync("COMMIT_FAILED", taskId, exception.Message);
            await TransitionAsync(taskId, AnalysisTaskStatus.RecoveryRequired, "RECOVERY_REQUIRED");
            return await FailedReloaded(taskId, "提交失败，需恢复：" + exception.Message);
        }

        AnalysisTask completed = (await _store.GetAnalysisTaskAsync(taskId)) ?? task;
        return AnalysisRunOutcome.Success(completed, output);
    }

    /// <summary>
    /// Rebuilds the authoritative run outcome for an already-persisted task by reading its committed
    /// artifacts back from SQLite. The UI calls this on browser refresh / Circuit reconnect so the
    /// rendered view is byte-identical to the post-run view — the backend SQLite state (JobId primary
    /// key) is the SINGLE source of truth, never the browser/page memory (AC-10 / 原则⑥/⑦).
    /// </summary>
    public async Task<AnalysisRunOutcome> LoadOutcomeAsync(string taskId)
    {
        AnalysisTask? task = await _store.GetAnalysisTaskAsync(taskId);
        if (task is null)
        {
            return AnalysisRunOutcome.Failed(taskId, "任务不存在。");
        }
        AnalysisOutput? output = await LoadOutputAsync(task);
        bool succeeded = JobLifecycle.IsFullyCompleted(task.Status);
        return AnalysisRunOutcome.Success(task, output)
            with { Succeeded = succeeded, ErrorMessage = succeeded ? null : "任务未在后端达到「已完成」状态（可能失败、进行中或需恢复）。" };
    }

    /// <summary>Reconstructs the split Engine output from the persisted artifacts. Returns
    /// <c>null</c> when the commit chain did not reach a fully separable result (e.g. a partial
    /// commit awaiting recovery), so the caller can fall back to the status-only view.</summary>
    private async Task<AnalysisOutput?> LoadOutputAsync(AnalysisTask task)
    {
        AnalysisResult? result = await _store.GetAnalysisResultByTaskAsync(task.TaskId);
        if (result is null)
        {
            return null;
        }
        RuntimeSnapshot? snapshot = await _store.GetRuntimeSnapshotByResultAsync(result.ResultId);
        EvidenceBundle? evidence = await _store.GetEvidenceBundleByResultAsync(result.ResultId);
        if (snapshot is null || evidence is null)
        {
            // Partially committed (recovery in progress): show the status branch only.
            return null;
        }
        List<ConflictObservation> conflicts = await _store.GetConflictObservationsByResultAsync(result.ResultId);
        return new AnalysisOutput(result, conflicts, snapshot, evidence);
    }

    /// <summary>
    /// Advances a task's persisted Job status. The transition is validated as forward-only
    /// (<see cref="JobLifecycle.CanAdvance"/>), persisted to SQLite, and recorded as a chained audit
    /// event — the three pillars of the P1 lifecycle authority.
    /// </summary>
    private async Task TransitionAsync(string taskId, string nextStatus, string auditAction)
    {
        AnalysisTask? current = await _store.GetAnalysisTaskAsync(taskId);
        string currentStatus = current?.Status ?? AnalysisTaskStatus.Draft;
        if (!JobLifecycle.CanAdvance(currentStatus, nextStatus))
        {
            throw new InvalidOperationException(
                $"Illegal forward-only Job transition: {currentStatus} -> {nextStatus}. " +
                "The P0-05 commit chain forbids this edge.");
        }
        await _store.UpdateAnalysisTaskStatusAsync(taskId, nextStatus);
        await _audit.AppendAsync(auditAction, taskId);
    }

    private async Task<AnalysisRunOutcome> FailedReloaded(string taskId, string error) =>
        AnalysisRunOutcome.Failed(taskId, error, await _store.GetAnalysisTaskAsync(taskId));
}

/// <summary>Outcome of a single analysis run.</summary>
/// <param name="Succeeded">True when the full commit chain completed.</param>
/// <param name="TaskId">The task identifier.</param>
/// <param name="ErrorMessage">Failure detail when <see cref="Succeeded"/> is false.</param>
/// <param name="Task">The (authoritative, re-read) analysis task.</param>
/// <param name="Output">The split Engine output (verbatim).</param>
public sealed record AnalysisRunOutcome(bool Succeeded, string TaskId, string? ErrorMessage, AnalysisTask? Task, AnalysisOutput? Output)
{
    public static AnalysisRunOutcome Success(AnalysisTask task, AnalysisOutput? output) => new(true, task.TaskId, null, task, output);
    public static AnalysisRunOutcome Failed(string taskId, string error) => new(false, taskId, error, null, null);
    public static AnalysisRunOutcome Failed(string taskId, string error, AnalysisTask? task) => new(false, taskId, error, task, null);
}
