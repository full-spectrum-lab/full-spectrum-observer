using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Audit + evidence read/append surface. The store enforces append-only (INSERT only) for
/// audit_records; this viewer never issues an update or delete (red line #7). Evidence export
/// returns the Engine-computed digest verbatim (red line #8).
/// </summary>
public sealed class AuditViewer
{
    private readonly ObserverStore _store;
    private readonly AuditContext _audit;

    public AuditViewer(ObserverStore store, AuditContext audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>Appends a chained audit record (append-only).</summary>
    public async Task AppendAsync(string action, string? taskId, string? details = null)
    {
        var prev = await _store.GetLatestAuditAsync();
        string at = SystemClock.UtcNow;
        string digest = _audit.ComputeDigest(prev?.AuditId, action, at, taskId);
        var record = _audit.Build(Ids.Next("AUD"), taskId, action, at, digest, prev?.AuditId);
        await _store.AppendAuditAsync(record);
        if (details is not null)
        {
            // A second chained record carries the human-readable detail without altering the first.
            var prev2 = await _store.GetLatestAuditAsync();
            string at2 = SystemClock.UtcNow;
            string digest2 = _audit.ComputeDigest(prev2?.AuditId, action + "_DETAIL", at2, taskId);
            var detail = _audit.Build(Ids.Next("AUD"), taskId, action + "_DETAIL", at2, digest2, prev2?.AuditId);
            await _store.AppendAuditAsync(detail);
        }
    }

    public Task<List<AuditRecord>> ListChainAsync(string? taskId = null) => _store.GetAuditChainAsync(taskId);

    public Task<AuditChainVerification> VerifyAsync() => _store.VerifyAuditChainAsync();

    /// <summary>Exports the evidence bundle for a task as a JSON document (digest verbatim).</summary>
    public async Task<string> ExportEvidenceAsync(string taskId)
    {
        AnalysisResult? result = await _store.GetAnalysisResultByTaskAsync(taskId);
        if (result is null)
        {
            throw new InvalidOperationException("无分析结果可导出。");
        }
        EvidenceBundle? evidence = await _store.GetEvidenceBundleByResultAsync(result.ResultId);
        var export = new
        {
            contract = "fs-observer/evidence-export/1",
            exported_at_utc = SystemClock.UtcNow,
            task_id = taskId,
            result_id = result.ResultId,
            unknown_state = result.UnknownState,
            hard_gate = result.HardGate,
            evidence_digest = evidence?.EvidenceDigest ?? string.Empty,
            references = evidence?.References.ToArray() ?? Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }
}
