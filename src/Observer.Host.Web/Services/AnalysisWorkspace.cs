using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Orchestrates a single analysis run: build the request envelope, preflight structure
/// validation, invoke the pinned Engine v1.5.0, then persist the result along the R1-D ordered
/// commit chain. On any contract / dependency failure it writes a WARNING audit and BLOCKS
/// persistence (fail-closed); it never downgrades or recomputes the Engine's conclusion.
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

    public AnalysisWorkspace(
        ObserverStore store,
        FullSpectrum.Observer.EngineFacade.EngineFacade engine,
        IntakeAdapter intake,
        OutputAdapter output,
        AuditViewer audit,
        SubjectCatalog subjects,
        KnowledgeCatalog knowledge)
    {
        _store = store;
        _engine = engine;
        _intake = intake;
        _output = output;
        _audit = audit;
        _subjects = subjects;
        _knowledge = knowledge;
    }

    public async Task<AnalysisTask> CreateDraftTaskAsync(
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention)
    {
        var task = AnalysisTask.Create(
            Ids.Next("TASK"),
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
        await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.Running);

        SubjectVersion? subjectVersion = await _subjects.GetVersionAsync(task.SubjectVersionId);
        if (subjectVersion is null)
        {
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PreflightFailed);
            return AnalysisRunOutcome.Failed(taskId, "主体版本缺失（预检查失败）。");
        }
        ObservedSubject? subject = await _subjects.GetSubjectAsync(subjectVersion.SubjectId);
        if (subject is null)
        {
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PreflightFailed);
            return AnalysisRunOutcome.Failed(taskId, "主体缺失（预检查失败）。");
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
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PreflightFailed);
            return AnalysisRunOutcome.Failed(taskId, "预检查失败：" + exception.Message);
        }
        await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PrecheckPassed);

        EngineResponse response;
        try
        {
            response = await _engine.AnalyzeAsync(envelope);
        }
        catch (DependencyMissingException exception)
        {
            // TC-NEW-003: Engine missing -> "依赖缺失/不可重放", block the task.
            await _audit.AppendAsync("ENGINE_DEPENDENCY_MISSING", taskId, exception.Message);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.EngineFailed);
            return AnalysisRunOutcome.Failed(taskId, "依赖缺失/不可重放：" + exception.Message);
        }
        catch (VersionBindingException exception)
        {
            await _audit.AppendAsync("VERSION_BINDING_FAILED", taskId, exception.Message);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidationFailed);
            return AnalysisRunOutcome.Failed(taskId, exception.Message);
        }
        catch (ContractViolationException exception)
        {
            // R1-B §5.2: response contract violated -> write WARNING audit, block persistence.
            await _audit.AppendAsync("CONTRACT_VIOLATION", taskId, exception.Message);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidationFailed);
            return AnalysisRunOutcome.Failed(taskId, "契约违约，阻断持久化：" + exception.Message);
        }

        await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.EngineCompleted);
        await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidated);

        AnalysisOutput output = _output.Parse(response, task, SystemClock.UtcNow);

        // R1-D ordered commit chain. Any failure leaves prior partial artifacts and enters RECOVERY_REQUIRED.
        try
        {
            await _store.InsertAnalysisResultAsync(output.Result);
            await _store.InsertRuntimeSnapshotAsync(output.Snapshot);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.SnapshotCommitted);

            await _store.InsertEvidenceBundleAsync(output.Evidence);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.ArtifactCommitted);

            await _store.InsertConflictObservationsAsync(output.Conflicts);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.ObservationCommitted);

            await _audit.AppendAsync("RESULT", taskId, $"replay_digest={response.ReplayRef?.Digest}");
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.AuditCommitted);

            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.Completed);
        }
        catch (Exception exception)
        {
            await _audit.AppendAsync("COMMIT_FAILED", taskId, exception.Message);
            await _store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.RecoveryRequired);
            return AnalysisRunOutcome.Failed(taskId, "提交失败，需恢复：" + exception.Message);
        }

        return AnalysisRunOutcome.Success(task, output);
    }
}

/// <summary>Outcome of a single analysis run.</summary>
/// <param name="Succeeded">True when the full commit chain completed.</param>
/// <param name="TaskId">The task identifier.</param>
/// <param name="ErrorMessage">Failure detail when <see cref="Succeeded"/> is false.</param>
/// <param name="Task">The analysis task.</param>
/// <param name="Output">The split Engine output (verbatim).</param>
public sealed record AnalysisRunOutcome(bool Succeeded, string TaskId, string? ErrorMessage, AnalysisTask? Task, AnalysisOutput? Output)
{
    public static AnalysisRunOutcome Success(AnalysisTask task, AnalysisOutput output) => new(true, task.TaskId, null, task, output);
    public static AnalysisRunOutcome Failed(string taskId, string error) => new(false, taskId, error, null, null);
}
