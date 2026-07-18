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
    /// This is what the operator navigates to (Console Access URL).
    /// </summary>
    public string? RequestedEndpoint { get; set; }

    /// <summary>
    /// Addresses Kestrel actually bound after startup, read from
    /// <c>IServerAddressesFeature.Addresses</c>. This is the authoritative binding fact —
    /// it reflects startup overrides, config overrides, IPv4/IPv6 differences and test-host
    /// substitution, not a static constant. Defaults to an empty list until the server starts;
    /// <see cref="GetVersionInfo"/> then falls back to <see cref="RequestedEndpoint"/>.
    /// </summary>
    public IReadOnlyList<string> ActualBoundEndpoints { get; set; } = new List<string>();

    /// <summary>
    /// Directory containing <c>release-manifest.json</c>. Defaults to the application base
    /// directory. Tests override this to point at a crafted manifest.
    /// </summary>
    public string ManifestDirectory { get; set; } = AppContext.BaseDirectory;

    public Task<StoreDiagnostics> GetStoreDiagnosticsAsync() => _store.GetDiagnosticsAsync();

    /// <summary>
    /// Pinned version info. Engine identity comes from the frozen <see cref="EngineV15Contract"/>;
    /// the artifact digest and build channel come from the single runtime source of truth
    /// (<c>release-manifest.json</c>), never from a source constant.
    /// </summary>
    public VersionInfo GetVersionInfo() => new(
        EngineV15Contract.EngineTag,
        EngineV15Contract.EngineCommit,
        ResolveArtifactDigest(),
        EngineV15Contract.AdapterVersion,
        EngineV15Contract.SchemaVersion,
        EngineV15Contract.SchemaDigest,
        EngineV15Contract.CompatibilityMatrixId,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Environment.Version.ToString(),
        RequestedEndpoint ?? "unknown",
        ActualBoundEndpoints.Count > 0 ? string.Join(" ; ", ActualBoundEndpoints) : (RequestedEndpoint ?? "unknown"),
        ResolveBuildChannel());

    /// <summary>
    /// Resolve the engine artifact digest from the single source of truth: release-manifest.json.
    /// Dev / unpublished builds (no manifest, or a manifest that declares UNPUBLISHED, or any
    /// non-64-hex value including a stale placeholder) report "UNPUBLISHED". The legacy source
    /// constant is never displayed.
    /// </summary>
    public string ResolveArtifactDigest()
    {
        var m = ReadManifest();
        return m.ArtifactDigest;
    }

    /// <summary>Resolve the build channel (RELEASE / DEVELOPMENT) from release-manifest.json.</summary>
    public string ResolveBuildChannel()
    {
        var m = ReadManifest();
        return m.BuildChannel;
    }

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

            var digest = root.TryGetProperty("artifact_digest", out var d) ? (d.GetString()?.Trim() ?? "") : "";
            var channel = root.TryGetProperty("build_channel", out var c) ? (c.GetString()?.Trim() ?? "") : "";

            // Single source of truth rules: only a valid 64-hex SHA-256 is a real release digest.
            // Anything else (empty, "UNPUBLISHED", a placeholder, or garbage) is reported as
            // UNPUBLISHED so the page never exposes an internal sentinel string.
            if (digest.Length == 0 || digest.Equals("UNPUBLISHED", StringComparison.OrdinalIgnoreCase) || !IsValidSha256(digest))
            {
                digest = "UNPUBLISHED";
            }
            if (channel.Length == 0)
            {
                channel = digest == "UNPUBLISHED" ? "DEVELOPMENT" : "RELEASE";
            }
            return new ReleaseManifest { ArtifactDigest = digest, BuildChannel = channel };
        }
        catch
        {
            return ReleaseManifest.DevUnpublished;
        }
    }

    private static bool IsValidSha256(string s) =>
        s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    private sealed record ReleaseManifest
    {
        public static readonly ReleaseManifest DevUnpublished = new() { ArtifactDigest = "UNPUBLISHED", BuildChannel = "DEVELOPMENT" };

        public string ArtifactDigest { get; init; } = "UNPUBLISHED";
        public string BuildChannel { get; init; } = "DEVELOPMENT";
    }
}

/// <summary>Snapshot of version / health metadata for the System Information page.</summary>
/// <param name="EngineTag">Pinned Engine tag (v1.5.0).</param>
/// <param name="EngineCommit">Pinned Engine commit (Gitee authoritative).</param>
/// <param name="EngineArtifactDigest">Engine artifact digest from release-manifest.json (UNPUBLISHED for dev).</param>
/// <param name="AdapterVersion">Observer adapter fixture version.</param>
/// <param name="SchemaVersion">Observer schema version.</param>
/// <param name="SchemaDigest">Observer schema digest (computed from Init.sql).</param>
/// <param name="CompatibilityMatrixId">v1.5 compatibility matrix id.</param>
/// <param name="AppVersion">Console assembly version.</param>
/// <param name="DotNetVersion">.NET runtime version.</param>
/// <param name="RequestedEndpoint">Endpoint the Console was launched with (Console Access URL).</param>
/// <param name="ActualBoundEndpoint">Addresses Kestrel actually bound at runtime (authoritative).</param>
/// <param name="BuildChannel">Build channel from release-manifest.json (RELEASE / DEVELOPMENT).</param>
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
    string RequestedEndpoint,
    string ActualBoundEndpoint,
    string BuildChannel);
