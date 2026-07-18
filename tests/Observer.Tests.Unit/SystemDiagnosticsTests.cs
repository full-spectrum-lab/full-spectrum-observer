using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M2-RUN-01 finding closure — single source of truth for the engine artifact digest, and
/// runtime port display sourced from the actually-bound Kestrel addresses (not a hardcoded
/// constant). These are pure logic tests against <see cref="SystemDiagnostics"/>.
/// </summary>
public sealed class SystemDiagnosticsTests
{
    private static SystemDiagnostics NewDiagnostics(string manifestDir)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "fso-sd-test-" + Path.GetRandomFileName() + ".db");
        var store = new ObserverStore(dbPath);
        return new SystemDiagnostics(store) { ManifestDirectory = manifestDir };
    }

    [Fact]
    public void Digest_without_manifest_reports_UNPUBLISHED_not_placeholder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var sd = NewDiagnostics(dir);
            var ver = sd.GetVersionInfo();
            ver.EngineArtifactDigest.Should().Be("UNPUBLISHED");
            ver.BuildChannel.Should().Be("DEVELOPMENT");
            ver.EngineArtifactDigest.Should().NotContain("PLACEHOLDER");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Digest_reads_valid_sha256_from_manifest_single_source_of_truth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        File.WriteAllText(Path.Combine(dir, "release-manifest.json"),
            "{\"artifact_digest\":\"" + sha + "\",\"build_channel\":\"RELEASE\"}");
        try
        {
            var sd = NewDiagnostics(dir);
            var ver = sd.GetVersionInfo();
            ver.EngineArtifactDigest.Should().Be(sha);
            ver.BuildChannel.Should().Be("RELEASE");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Digest_treats_non_64hex_as_UNPUBLISHED_and_never_exposes_placeholder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "release-manifest.json"),
            "{\"artifact_digest\":\"PLACEHOLDER_PENDING_PUBLISHED_ARTIFACT_SHA256\",\"build_channel\":\"RELEASE\"}");
        try
        {
            var sd = NewDiagnostics(dir);
            var ver = sd.GetVersionInfo();
            ver.EngineArtifactDigest.Should().Be("UNPUBLISHED");
            ver.EngineArtifactDigest.Should().NotContain("PLACEHOLDER");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Port_display_uses_actual_bound_endpoint_not_hardcoded_5180()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var sd = NewDiagnostics(dir);
            sd.RequestedEndpoint = "http://127.0.0.1:6778";
            sd.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:6778" };
            var ver = sd.GetVersionInfo();
            ver.RequestedEndpoint.Should().Be("http://127.0.0.1:6778");
            ver.ActualBoundEndpoint.Should().Be("http://127.0.0.1:6778");
            ver.ActualBoundEndpoint.Should().NotContain("5180");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Port_display_changes_with_instance_and_never_hardcodes_5180()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var sd = NewDiagnostics(dir);
            sd.RequestedEndpoint = "http://127.0.0.1:8123";
            sd.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:8123" };
            var ver = sd.GetVersionInfo();
            ver.ActualBoundEndpoint.Should().Be("http://127.0.0.1:8123");

            // second instance on a different port
            var sd2 = NewDiagnostics(dir);
            sd2.RequestedEndpoint = "http://127.0.0.1:9555";
            sd2.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:9555" };
            var ver2 = sd2.GetVersionInfo();
            ver2.ActualBoundEndpoint.Should().Be("http://127.0.0.1:9555");
            ver2.ActualBoundEndpoint.Should().NotBe(ver.ActualBoundEndpoint);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Port_display_falls_back_to_requested_when_no_actual_binding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var sd = NewDiagnostics(dir);
            sd.RequestedEndpoint = "http://127.0.0.1:7777";
            sd.ActualBoundEndpoints = new List<string>();
            var ver = sd.GetVersionInfo();
            ver.ActualBoundEndpoint.Should().Be("http://127.0.0.1:7777");
        }
        finally { Directory.Delete(dir, true); }
    }
}
