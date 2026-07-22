#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>System information / health read surface (no governance logic).</summary>
public sealed class SystemDiagnostics
{
    /// <summary>
    /// Frozen Engine v1.5.0 source digest baseline. This is a pinned, well-known constant for the
    /// Engine source tree; it is always available and is displayed as engine_source_digest.
    /// It lives on the read-only diagnostics surface and does NOT modify the Engine facade contract.
    /// </summary>
    public const string EngineSourceDigestBaseline = "9646d5742fe644522b6bf17dd5eab3cdf4c42ce87f4bcfa4b61284ee7a1e321c";

    private readonly ObserverStore _store;
    private readonly object _manifestLock = new();
    private ReleaseManifest? _manifest;

    public SystemDiagnostics(ObserverStore store)
    {
        _store = store;
    }

    /// <summary>Resolved, stable, absolute data directory in use by this process.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Endpoint the Console was launched with (the Launcher supplies it via <c>--urls</c>).
    /// This is the operator-supplied access URL and is NOT the authoritative listener address.
    /// </summary>
    public string? RequestedEndpoint { get; set; }

    /// <summary>
    /// Addresses Kestrel actually bound after startup, read from
    /// <c>IServerAddressesFeature.Addresses</c>. This is the authoritative binding fact —
    /// it reflects startup overrides, config overrides, IPv4/IPv6 differences and test-host
    /// substitution, not a static constant. Defaults to an empty list until the server starts.
    /// </summary>
    public IReadOnlyList<string> ActualBoundEndpoints { get; set; } = new List<string>();

    /// <summary>
    /// Directory containing <c>release-manifest.json</c>. Defaults to the application base
    /// directory. Tests override this to point at a crafted manifest.
    /// </summary>
    public string ManifestDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// Optional path to a package-EXTERNAL release identity file (e.g.
    /// <c>V030_RELEASE_CANDIDATE_IDENTITY.json</c>). This file lives OUTSIDE the package and is the
    /// authoritative source for the release identity — including the package SHA-256 — so the
    /// in-package <c>release-manifest.json</c> never has to self-reference the package's own hash
    /// (a ZIP cannot sign its own contents before it is hashed).
    /// When set and readable → source = EXTERNAL_RELEASE_IDENTITY.
    /// When set but missing/unreadable → source = NOT_AVAILABLE.
    /// When unset → falls back to the package-internal manifest, then the dev worktree.
    /// The candidate commit and package SHA are NEVER hardcoded here.
    /// </summary>
    public string? ExternalIdentityPath { get; set; }

    public Task<StoreDiagnostics> GetStoreDiagnosticsAsync() => _store.GetDiagnosticsAsync();

    /// <summary>
    /// Pinned version info. Engine identity comes from the frozen <see cref="EngineV15Contract"/>.
    /// observer_* identity and the package/observer sha come from the single runtime source of
    /// truth (<c>release-manifest.json</c>); they are never hardcoded. The engine source digest
    /// is the pinned baseline constant. Dev / unpublished builds report empty strings, which the
    /// pages render as NOT_AVAILABLE with a reason.
    /// </summary>
    public VersionInfo GetVersionInfo()
    {
        var ident = ResolveIdentity();
        return new(
            EngineV15Contract.EngineTag,
            EngineV15Contract.EngineCommit,
            EngineSourceDigestBaseline,
            ident.PackageSha256,
            EngineV15Contract.AdapterVersion,
            EngineV15Contract.SchemaVersion,
            EngineV15Contract.SchemaDigest,
            EngineV15Contract.CompatibilityMatrixId,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Environment.Version.ToString(),
            RequestedEndpoint ?? "unknown",
            ActualBoundEndpoints,
            ident.ObserverVersion,
            ident.ObserverCommit,
            ident.BuildChannel,
            ident.Source);
    }

    /// <summary>
    /// Resolve the Observer identity with an explicit, honest source label:
    /// EXTERNAL_RELEASE_IDENTITY (package-external identity file),
    /// PACKAGE_MANIFEST (package-internal release-manifest.json),
    /// DEVELOPMENT_WORKTREE (dev build, no manifest), or
    /// NOT_AVAILABLE (an external identity was expected but could not be loaded).
    /// The package SHA-256 is taken ONLY from the external identity file — the internal manifest
    /// never self-references the package's own hash.
    /// </summary>
    private ResolvedIdentity ResolveIdentity()
    {
        // The package-external identity file is the authoritative source for the release identity,
        // including the package SHA-256. It may be supplied explicitly (tests) or via the
        // OBSERVER_RELEASE_IDENTITY_PATH environment variable (the Launcher / RC smoke passes it).
        // This keeps the in-package manifest free of any self-referencing package hash.
        string? extPath = ExternalIdentityPath
            ?? Environment.GetEnvironmentVariable("OBSERVER_RELEASE_IDENTITY_PATH");
        if (!string.IsNullOrWhiteSpace(extPath))
        {
            var ext = File.Exists(extPath) ? LoadExternalIdentity(extPath) : null;
            if (ext is not null)
            {
                return new ResolvedIdentity(
                    ext.ObserverVersion,
                    ext.ObserverCommit,
                    ext.PackageSha256,
                    NormalizeChannel(ext.BuildChannel),
                    "EXTERNAL_RELEASE_IDENTITY");
            }

            // An external identity was expected but could not be established.
            return new ResolvedIdentity("", "", "", "DEVELOPMENT", "NOT_AVAILABLE");
        }

        var m = ReadManifest();
        if (m.Found)
        {
            // Package-internal manifest is authoritative for version/commit/channel, but NOT for the
            // package SHA-256 (it cannot sign its own contents) — that stays NOT_AVAILABLE here.
            return new ResolvedIdentity(m.ObserverVersion, m.ObserverCommit, "", m.BuildChannel, "PACKAGE_MANIFEST");
        }

        return new ResolvedIdentity("", "", "", "DEVELOPMENT", "DEVELOPMENT_WORKTREE");
    }

    private static ExternalReleaseIdentity? LoadExternalIdentity(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new ExternalReleaseIdentity
            {
                ObserverVersion = root.TryGetProperty("observer_version", out var ov) ? (ov.GetString()?.Trim() ?? "") : "",
                ObserverCommit = root.TryGetProperty("observer_commit", out var oc) ? (oc.GetString()?.Trim() ?? "") : "",
                PackageSha256 = root.TryGetProperty("package_sha256", out var ps) ? (ps.GetString()?.Trim() ?? "") : "",
                BuildChannel = root.TryGetProperty("build_channel", out var c) ? (c.GetString()?.Trim() ?? "") : "",
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Restrict build_channel to the three explicit, non-promotional values.</summary>
    private static string NormalizeChannel(string? channel)
    {
        var allowed = new HashSet<string> { "DEVELOPMENT", "RELEASE_CANDIDATE", "RELEASE" };
        var trimmed = (channel ?? "").Trim();
        return allowed.Contains(trimmed) ? trimmed : "DEVELOPMENT";
    }

    public string ResolveObserverVersion() => ResolveIdentity().ObserverVersion;
    public string ResolveObserverCommit() => ResolveIdentity().ObserverCommit;
    public string ResolveObserverPackageSha256() => ResolveIdentity().PackageSha256;
    public string ResolveBuildChannel() => ResolveIdentity().BuildChannel;
    public string ResolveIdentitySource() => ResolveIdentity().Source;

    private ReleaseManifest ReadManifest()
    {
        lock (_manifestLock)
        {
            if (_manifest is not null) return _manifest;
            _manifest = LoadManifest();
            return _manifest;
        }
    }

    private ReleaseManifest LoadManifest()
    {
        try
        {
            var path = Path.Combine(ManifestDirectory, "release-manifest.json");
            if (!File.Exists(path)) return ReleaseManifest.DevUnpublished;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var channel = root.TryGetProperty("build_channel", out var c) ? (c.GetString()?.Trim() ?? "") : "";
            var observerVersion = root.TryGetProperty("observer_version", out var ov) ? (ov.GetString()?.Trim() ?? "") : "";
            var observerCommit = root.TryGetProperty("observer_commit", out var oc) ? (oc.GetString()?.Trim() ?? "") : "";
            var observerPackageSha = root.TryGetProperty("observer_package_sha256", out var ops) ? (ops.GetString()?.Trim() ?? "") : "";

            // NOTE: observer_package_sha256 in the package-internal manifest is intentionally NOT the
            // package's own SHA-256. A ZIP cannot sign its own contents before it is hashed, so the
            // authoritative package_sha256 lives in the package-EXTERNAL identity file. ResolveIdentity
            // therefore never surfaces this internal value as the package SHA.
            return new ReleaseManifest
            {
                Found = true,
                BuildChannel = NormalizeChannel(channel),
                ObserverVersion = observerVersion,
                ObserverCommit = observerCommit,
                ObserverPackageSha256 = observerPackageSha,
            };
        }
        catch
        {
            return ReleaseManifest.DevUnpublished;
        }
    }

    /// <summary>True when the address is a loopback binding (IPv4 127.0.0.1 / IPv6 ::1 / localhost).</summary>
    public static bool IsLoopbackAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        var h = uri.Host;
        return h is "127.0.0.1" or "::1" or "localhost" or "[::1]";
    }

    /// <summary>
    /// Security note derived strictly from the actually-bound endpoints at runtime. It never
    /// asserts an unprovable absolute claim (e.g. "never exposes 0.0.0.0"); it reports what was
    /// detected.
    /// </summary>
    public string BuildListenerSecurityNote()
    {
        var nonLoop = ActualBoundEndpoints.Where(e => !IsLoopbackAddress(e)).ToList();
        return nonLoop.Count == 0
            ? "当前运行实例仅检测到环回监听，未检测到非环回监听。"
            : "检测到非环回监听端点：" + string.Join("; ", nonLoop) + "。请确认网络边界与访问控制。";
    }

    /// <summary>
    /// Pure, testable mapping from an audit-chain verification result to the exact display label.
    /// Only a non-empty chain that passes continuous-hash verification is "完整".
    /// An empty chain (0 records) is "尚未建立" — never "完整".
    /// Anything broken is "断裂".
    /// </summary>
    public static string AuditChainStateLabel(AuditChainVerification chain) => chain switch
    {
        { IsValid: true, RecordCount: > 0 } => "完整",
        { IsValid: true, RecordCount: 0 } => "尚未建立",
        _ => "断裂",
    };

    private sealed record ReleaseManifest
    {
        public static readonly ReleaseManifest DevUnpublished = new() { Found = false, BuildChannel = "DEVELOPMENT" };

        public bool Found { get; init; }
        public string BuildChannel { get; init; } = "DEVELOPMENT";
        public string ObserverVersion { get; init; } = "";
        public string ObserverCommit { get; init; } = "";
        public string ObserverPackageSha256 { get; init; } = "";
    }

    private sealed record ExternalReleaseIdentity
    {
        public string ObserverVersion { get; init; } = "";
        public string ObserverCommit { get; init; } = "";
        public string PackageSha256 { get; init; } = "";
        public string BuildChannel { get; init; } = "";
    }

    private sealed record ResolvedIdentity(string ObserverVersion, string ObserverCommit, string PackageSha256, string BuildChannel, string Source);
}

/// <summary>Snapshot of version / health metadata for the System Information page.</summary>
/// <param name="EngineTag">Pinned Engine tag (v1.5.0).</param>
/// <param name="EngineCommit">Pinned Engine commit (Gitee authoritative).</param>
/// <param name="EngineSourceDigest">Frozen Engine v1.5.0 source digest baseline.</param>
/// <param name="ObserverPackageSha256">Observer package SHA-256 (from the package-external identity file; empty for dev/manifest-only).</param>
/// <param name="AdapterVersion">Observer adapter fixture version.</param>
/// <param name="SchemaVersion">Observer schema version.</param>
/// <param name="SchemaDigest">Observer schema digest (computed from Init.sql).</param>
/// <param name="CompatibilityMatrixId">v1.5 compatibility matrix id.</param>
/// <param name="AppVersion">Console assembly version.</param>
/// <param name="DotNetVersion">.NET runtime version.</param>
/// <param name="RequestedEndpoint">Endpoint the Console was launched with (startup arg, not the authoritative listener).</param>
/// <param name="ActualBoundEndpoints">Addresses Kestrel actually bound at runtime (authoritative).</param>
/// <param name="ObserverVersion">Observer version (from manifest/external identity; empty for dev).</param>
/// <param name="ObserverCommit">Observer commit (from manifest/external identity; empty for dev).</param>
/// <param name="BuildChannel">Build channel (DEVELOPMENT / RELEASE_CANDIDATE / RELEASE).</param>
/// <param name="IdentitySource">Explicit, honest origin of the Observer identity (EXTERNAL_RELEASE_IDENTITY / PACKAGE_MANIFEST / DEVELOPMENT_WORKTREE / NOT_AVAILABLE).</param>
public sealed record VersionInfo(
    string EngineTag,
    string EngineCommit,
    string EngineSourceDigest,
    string ObserverPackageSha256,
    string AdapterVersion,
    string SchemaVersion,
    string SchemaDigest,
    string CompatibilityMatrixId,
    string AppVersion,
    string DotNetVersion,
    string RequestedEndpoint,
    IReadOnlyList<string> ActualBoundEndpoints,
    string ObserverVersion,
    string ObserverCommit,
    string BuildChannel,
    string IdentitySource);
