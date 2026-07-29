using System.Collections.Generic;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;

#nullable enable

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Cross-page orchestrator. It holds the catalogs and the analysis workspace and exposes them to
/// the pages. It contains NO governance logic — it only coordinates calls to the stores and the
/// Engine facade (which owns all deterministic governance computation).
/// </summary>
public sealed class Orchestrator
{
    public SubjectCatalog Subjects { get; }

    public KnowledgeCatalog Knowledge { get; }

    public AnalysisWorkspace Workspace { get; }

    public AuditViewer Audit { get; }

    public SystemDiagnostics Diagnostics { get; }

    public Orchestrator(SubjectCatalog subjects, KnowledgeCatalog knowledge, AnalysisWorkspace workspace, AuditViewer audit, SystemDiagnostics diagnostics)
    {
        Subjects = subjects;
        Knowledge = knowledge;
        Workspace = workspace;
        Audit = audit;
        Diagnostics = diagnostics;
    }

    /// <summary>Runs a previously created analysis task (delegates to the workspace).</summary>
    public Task<AnalysisRunOutcome> RunAnalysisAsync(string taskId) => Workspace.RunAnalysisAsync(taskId);

    /// <summary>Idempotent create-and-run keyed by JobId + request fingerprint (delegates to the workspace).</summary>
    public Task<AnalysisRunOutcome> CreateAndRunAsync(
        string? requestedJobId,
        string subjectVersionId,
        IReadOnlyList<string> knowledgeVersionIds,
        RawAnalysisInput input,
        RetentionMode retention) =>
        Workspace.CreateAndRunAsync(requestedJobId, subjectVersionId, knowledgeVersionIds, input, retention);
}
