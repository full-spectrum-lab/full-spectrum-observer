using System;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>System information / health read surface (no governance logic).</summary>
public sealed class SystemDiagnostics
{
    private readonly ObserverStore _store;

    public SystemDiagnostics(ObserverStore store)
    {
        _store = store;
    }

    public Task<StoreDiagnostics> GetStoreDiagnosticsAsync() => _store.GetDiagnosticsAsync();

    /// <summary>Pinned version info. Engine identity comes from the frozen EngineV15Contract.</summary>
    public VersionInfo GetVersionInfo() => new(
        EngineV15Contract.EngineTag,
        EngineV15Contract.EngineCommit,
        EngineV15Contract.EngineArtifactDigest,
        EngineV15Contract.AdapterVersion,
        EngineV15Contract.SchemaVersion,
        EngineV15Contract.SchemaDigest,
        EngineV15Contract.CompatibilityMatrixId,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Environment.Version.ToString(),
        "127.0.0.1 / [::1]:5180 (loopback-only)");
}

/// <summary>Snapshot of version / health metadata for the System Information page.</summary>
/// <param name="EngineTag">Pinned Engine tag (v1.5.0).</param>
/// <param name="EngineCommit">Pinned Engine commit (Gitee authoritative).</param>
/// <param name="EngineArtifactDigest">Published artifact digest; PLACEHOLDER until GO-6.</param>
/// <param name="AdapterVersion">Observer adapter fixture version.</param>
/// <param name="SchemaVersion">Observer schema version.</param>
/// <param name="SchemaDigest">Observer schema digest (computed from Init.sql).</param>
/// <param name="CompatibilityMatrixId">v1.5 compatibility matrix id.</param>
/// <param name="AppVersion">Console assembly version.</param>
/// <param name="DotNetVersion">.NET runtime version.</param>
/// <param name="LoopbackBinding">Loopback binding description.</param>
public sealed record VersionInfo(
    string EngineTag,
    string EngineCommit,
    string EngineArtifactDigest,
    string AdapterVersion,
    string SchemaVersion,
    string SchemaDigest,
    string CompatibilityMatrixId,
    string AppVersion,
    string DotNetVersion,
    string LoopbackBinding);
