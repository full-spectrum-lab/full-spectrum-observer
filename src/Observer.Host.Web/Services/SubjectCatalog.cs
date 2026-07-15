using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Subject lifecycle catalog. Enforces ADR-001: editing never mutates an Active version — it
/// creates a new Draft, then activates (which retires the previous Active and writes an audit).
/// Red line #1: subjects are analysis context only; no login/auth fields are involved.
/// </summary>
public sealed class SubjectCatalog
{
    private readonly ObserverStore _store;
    private readonly AuditContext _audit;

    public SubjectCatalog(ObserverStore store, AuditContext audit)
    {
        _store = store;
        _audit = audit;
    }

    public Task<List<ObservedSubject>> ListAsync() => _store.GetSubjectsAsync();
    public Task<ObservedSubject?> GetSubjectAsync(string id) => _store.GetSubjectAsync(id);
    public Task<SubjectVersion?> GetVersionAsync(string versionId) => _store.GetSubjectVersionAsync(versionId);
    public Task<List<SubjectVersion>> ListVersionsAsync(string subjectId) => _store.GetSubjectVersionsAsync(subjectId);
    public Task<SubjectVersion?> GetActiveVersionAsync(string subjectId) => _store.GetActiveSubjectVersionAsync(subjectId);

    public async Task CreateWithDraftAsync(string localSubjectId, string subjectType, string mode, string? concentrationTier, string declarationJson)
    {
        var subject = new ObservedSubject
        {
            LocalSubjectId = localSubjectId,
            SubjectType = subjectType,
            Mode = mode,
            ConcentrationTier = concentrationTier,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertSubjectAsync(subject);

        int seq = await _store.GetNextSubjectSeqAsync(localSubjectId);
        var version = new SubjectVersion
        {
            VersionId = Ids.Next("SUBV"),
            SubjectId = localSubjectId,
            Status = "Draft",
            Seq = seq,
            Payload = declarationJson,
            SchemaVersion = EngineV15Contract.SchemaVersion,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertSubjectVersionAsync(version);
        await _audit.AppendAsync("CREATE_SUBJECT", null, $"subject={localSubjectId}");
    }

    public async Task ActivateAsync(string subjectId, string versionId)
    {
        AuditRecord audit = BuildAudit("ACTIVATE", null);
        await _store.ActivateSubjectVersionAsync(versionId, SystemClock.UtcNow, audit);
    }

    public async Task RetireAsync(string subjectId, string versionId)
    {
        AuditRecord audit = BuildAudit("RETIRE", null);
        await _store.RetireSubjectVersionAsync(versionId, SystemClock.UtcNow, audit);
    }

    public async Task<string> CopyAsDraftAsync(string subjectId, string versionId)
    {
        SubjectVersion? source = await _store.GetSubjectVersionAsync(versionId);
        if (source is null)
        {
            throw new InvalidOperationException("源版本不存在。");
        }
        int seq = await _store.GetNextSubjectSeqAsync(subjectId);
        var draft = new SubjectVersion
        {
            VersionId = Ids.Next("SUBV"),
            SubjectId = subjectId,
            Status = "Draft",
            Seq = seq,
            Payload = source.Payload,
            SchemaVersion = source.SchemaVersion,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertSubjectVersionAsync(draft);
        await _audit.AppendAsync("COPY_DRAFT", null, $"from={versionId}");
        return draft.VersionId;
    }

    private AuditRecord BuildAudit(string action, string? taskId)
    {
        var prev = _store.GetLatestAuditAsync().GetAwaiter().GetResult();
        string at = SystemClock.UtcNow;
        string digest = _audit.ComputeDigest(prev?.AuditId, action, at, taskId);
        return _audit.Build(Ids.Next("AUD"), taskId, action, at, digest, prev?.AuditId);
    }
}
