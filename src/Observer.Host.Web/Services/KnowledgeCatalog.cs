using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Knowledge source lifecycle catalog. On import the content digest is COMPUTED (sha256 of the
/// content) — never trusted from user input — so it can never be forged (red line #8). Activation
/// follows the same immutable lifecycle as subjects (ADR-001).
/// </summary>
public sealed class KnowledgeCatalog
{
    private readonly ObserverStore _store;
    private readonly AuditContext _audit;
    private readonly AuditViewer _viewer;

    public KnowledgeCatalog(ObserverStore store, AuditContext audit, AuditViewer viewer)
    {
        _store = store;
        _audit = audit;
        _viewer = viewer;
    }

    public Task<List<KnowledgeSource>> ListAsync() => _store.GetKnowledgeSourcesAsync();
    public Task<KnowledgeSource?> GetSourceAsync(string id) => _store.GetKnowledgeSourceAsync(id);
    public Task<KnowledgeSourceVersion?> GetVersionAsync(string versionId) => _store.GetKnowledgeSourceVersionAsync(versionId);
    public Task<List<KnowledgeSourceVersion>> ListVersionsAsync(string sourceId) => _store.GetKnowledgeSourceVersionsAsync(sourceId);
    public Task<KnowledgeSourceVersion?> GetActiveVersionAsync(string sourceId) => _store.GetActiveKnowledgeSourceVersionAsync(sourceId);

    /// <summary>Imports a knowledge source + Draft version. The digest is computed from the content.</summary>
    public async Task ImportAsync(string sourceId, string libraryId, string name, string applicability, string contentJson)
    {
        var source = new KnowledgeSource
        {
            SourceId = sourceId,
            LibraryId = libraryId,
            Name = name,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertKnowledgeSourceAsync(source);

        int seq = await _store.GetNextKnowledgeSeqAsync(sourceId);
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentJson)));
        var version = new KnowledgeSourceVersion
        {
            VersionId = Ids.Next("KSV"),
            SourceId = sourceId,
            Digest = digest,
            Applicability = applicability,
            Status = "Draft",
            Seq = seq,
            Payload = contentJson,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertKnowledgeSourceVersionAsync(version);
        await _viewer.AppendAsync("IMPORT_KNOWLEDGE", null, $"source={sourceId} digest={digest}");
    }

    public async Task ActivateAsync(string sourceId, string versionId)
    {
        AuditRecord audit = BuildAudit("ACTIVATE_KNOWLEDGE", null);
        await _store.ActivateKnowledgeSourceVersionAsync(versionId, SystemClock.UtcNow, audit);
    }

    public async Task<string> CopyAsDraftAsync(string sourceId, string versionId)
    {
        KnowledgeSourceVersion? source = await _store.GetKnowledgeSourceVersionAsync(versionId);
        if (source is null)
        {
            throw new InvalidOperationException("源版本不存在。");
        }
        int seq = await _store.GetNextKnowledgeSeqAsync(sourceId);
        var draft = new KnowledgeSourceVersion
        {
            VersionId = Ids.Next("KSV"),
            SourceId = sourceId,
            Digest = source.Digest,
            Applicability = source.Applicability,
            Status = "Draft",
            Seq = seq,
            Payload = source.Payload,
            CreatedAt = SystemClock.UtcNow,
        };
        await _store.InsertKnowledgeSourceVersionAsync(draft);
        await _viewer.AppendAsync("COPY_KNOWLEDGE_DRAFT", null, $"from={versionId}");
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
