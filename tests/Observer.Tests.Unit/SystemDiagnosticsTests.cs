using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Console "运行身份与空状态语义修正" 验收测试。覆盖：EngineSourceDigest 固定基线、
/// Observer 身份从 Manifest / 包外身份文件读取、Manifest 缺失为 NOT_AVAILABLE、
/// build_channel 三值白名单与非法值归一、ActualBoundEndpoints 为列表、环回/非环回安全说明、
/// 审计链空状态语义（0 条不显示"完整"，有记录且校验通过才"完整"）。
/// </summary>
public sealed class SystemDiagnosticsTests
{
    private static readonly string ValidSha =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"; // 64 hex

    private static SystemDiagnostics NewDiagnostics(string manifestDir, string? externalIdentityPath = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "fso-sd-test-" + Path.GetRandomFileName() + ".db");
        var store = new ObserverStore(dbPath);
        return new SystemDiagnostics(store)
        {
            ManifestDirectory = manifestDir,
            ExternalIdentityPath = externalIdentityPath,
        };
    }

    private static string WriteManifest(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "release-manifest.json");
        File.WriteAllText(path, json);
        return path;
    }

    // 1) EngineSourceDigest 正确显示固定基线
    [Fact]
    public void EngineSourceDigest_shows_pinned_baseline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.GetVersionInfo().EngineSourceDigest.Should().Be(SystemDiagnostics.EngineSourceDigestBaseline);
        }
        finally { Directory.Delete(dir, true); }
    }

    // 2) ObserverVersion 从 Manifest 读取
    [Fact]
    public void ObserverVersion_reads_from_manifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            WriteManifest(dir, "{\"observer_version\":\"v0.3.0-rc1\",\"build_channel\":\"RELEASE_CANDIDATE\"}");
            var sd = NewDiagnostics(dir);
            var ver = sd.GetVersionInfo();
            ver.ObserverVersion.Should().Be("v0.3.0-rc1");
            sd.ResolveIdentitySource().Should().Be("PACKAGE_MANIFEST");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 3) ObserverCommit 从 Manifest 读取
    [Fact]
    public void ObserverCommit_reads_from_manifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            WriteManifest(dir, "{\"observer_commit\":\"86e6f0f07d8d6a88aa7a3422f153d2b7b38fb770\",\"build_channel\":\"RELEASE_CANDIDATE\"}");
            var sd = NewDiagnostics(dir);
            sd.GetVersionInfo().ObserverCommit.Should().Be("86e6f0f07d8d6a88aa7a3422f153d2b7b38fb770");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 4) ObserverPackageSha256 从合法外部身份来源读取
    [Fact]
    public void ObserverPackageSha256_reads_from_external_identity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        var extDir = Path.Combine(Path.GetTempPath(), "fso-ext-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(extDir);
            var extPath = Path.Combine(extDir, "V030_RELEASE_CANDIDATE_IDENTITY.json");
            File.WriteAllText(extPath,
                "{\"observer_version\":\"v0.3.0-rc1\"," +
                "\"observer_commit\":\"86e6f0f07d8d6a88aa7a3422f153d2b7b38fb770\"," +
                "\"package_sha256\":\"" + ValidSha + "\"," +
                "\"build_channel\":\"RELEASE_CANDIDATE\"}");

            var sd = NewDiagnostics(dir, extPath);
            var ver = sd.GetVersionInfo();
            ver.ObserverPackageSha256.Should().Be(ValidSha);
            sd.ResolveIdentitySource().Should().Be("EXTERNAL_RELEASE_IDENTITY");
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(extDir, true);
        }
    }

    // 5) Manifest 缺失时显示 NOT_AVAILABLE
    [Fact]
    public void Missing_manifest_reports_NOT_AVAILABLE_and_dev_worktree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir); // no release-manifest.json, no external identity
            var sd = NewDiagnostics(dir);
            var ver = sd.GetVersionInfo();
            ver.ObserverVersion.Should().BeEmpty();
            ver.ObserverCommit.Should().BeEmpty();
            ver.ObserverPackageSha256.Should().BeEmpty();
            ver.IdentitySource.Should().Be("DEVELOPMENT_WORKTREE");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 6) build_channel 只允许 DEVELOPMENT / RELEASE_CANDIDATE / RELEASE
    [Theory]
    [InlineData("DEVELOPMENT")]
    [InlineData("RELEASE_CANDIDATE")]
    [InlineData("RELEASE")]
    public void BuildChannel_accepts_only_three_allowed_values(string channel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            WriteManifest(dir, "{\"build_channel\":\"" + channel + "\"}");
            var sd = NewDiagnostics(dir);
            sd.GetVersionInfo().BuildChannel.Should().Be(channel);
        }
        finally { Directory.Delete(dir, true); }
    }

    // 7) 非法 build_channel 不得伪装成 RELEASE
    [Theory]
    [InlineData("RELEASED")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("released")]
    public void Illegal_build_channel_normalizes_to_DEVELOPMENT_not_RELEASE(string illegal)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            WriteManifest(dir, "{\"build_channel\":\"" + illegal + "\"}");
            var sd = NewDiagnostics(dir);
            var channel = sd.GetVersionInfo().BuildChannel;
            channel.Should().Be("DEVELOPMENT");
            channel.Should().NotBe("RELEASE");
            channel.Should().NotBe("RELEASED");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 7b) 包外身份文件中的非法 build_channel 同样归一为 DEVELOPMENT
    [Fact]
    public void External_identity_with_illegal_channel_normalizes_to_DEVELOPMENT()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        var extDir = Path.Combine(Path.GetTempPath(), "fso-ext-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(extDir);
            var extPath = Path.Combine(extDir, "id.json");
            File.WriteAllText(extPath,
                "{\"observer_version\":\"v0.3.0-rc1\",\"package_sha256\":\"" + ValidSha + "\",\"build_channel\":\"RELEASED\"}");

            var sd = NewDiagnostics(dir, extPath);
            var channel = sd.GetVersionInfo().BuildChannel;
            channel.Should().Be("DEVELOPMENT");
            channel.Should().NotBe("RELEASED");
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(extDir, true);
        }
    }

    // 8) ActualBoundEndpoints 为列表
    [Fact]
    public void ActualBoundEndpoints_is_a_list_of_runtime_endpoints()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:11476", "http://[::1]:11476" };
            var ver = sd.GetVersionInfo();
            ver.ActualBoundEndpoints.Should().BeAssignableTo<IReadOnlyList<string>>();
            ver.ActualBoundEndpoints.Count.Should().Be(2);
            ver.ActualBoundEndpoints.Should().Contain("http://127.0.0.1:11476");
            ver.ActualBoundEndpoints.Should().Contain("http://[::1]:11476");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 9) 只有环回端点时安全状态为仅环回
    [Fact]
    public void Loopback_only_endpoints_report_loopback_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:11476", "http://[::1]:11476" };
            var note = sd.BuildListenerSecurityNote();
            note.Should().Contain("仅检测到环回监听");
            note.Should().Contain("未检测到非环回监听");
            note.Should().NotContain("检测到非环回监听端点");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 10) 存在非环回端点时必须明确警告
    [Fact]
    public void Non_loopback_endpoint_triggers_explicit_warning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://10.0.0.5:11476" };
            var note = sd.BuildListenerSecurityNote();
            note.Should().Contain("非环回");
            note.Should().NotContain("仅检测到环回监听");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 11) 审计记录为 0 时不得显示"完整"
    [Fact]
    public void Audit_chain_with_zero_records_is_NOT_complete()
    {
        var empty = new AuditChainVerification(true, 0, null, "");
        var label = SystemDiagnostics.AuditChainStateLabel(empty);
        label.Should().Be("尚未建立");
        label.Should().NotBe("完整");
    }

    // 12) 有记录且校验通过时才显示"完整"；断裂链显示"断裂"
    [Fact]
    public void Audit_chain_valid_with_records_is_complete_broken_is_not()
    {
        var valid = new AuditChainVerification(true, 5, null, "");
        SystemDiagnostics.AuditChainStateLabel(valid).Should().Be("完整");

        var broken = new AuditChainVerification(false, 5, "AUD-3", "tampered");
        SystemDiagnostics.AuditChainStateLabel(broken).Should().Be("断裂");
    }

    // 4b) 包外身份文件配置但缺失 → source = NOT_AVAILABLE（不臆造身份）
    [Fact]
    public void External_identity_expected_but_missing_reports_NOT_AVAILABLE()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        var missing = Path.Combine(Path.GetTempPath(), "fso-missing-" + Path.GetRandomFileName() + ".json");
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir, missing);
            var ver = sd.GetVersionInfo();
            ver.IdentitySource.Should().Be("NOT_AVAILABLE");
            ver.ObserverVersion.Should().BeEmpty();
            ver.ObserverPackageSha256.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    // 13) localhost 必须解析为实际环回绑定，绝不得作为"实际端点"显示
    [Fact]
    public void Localhost_endpoint_resolves_to_actual_loopback_bindings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://localhost:11476" };
            var resolved = sd.GetActualListenerEndpoints();
            resolved.Should().NotContain("http://localhost:11476");
            resolved.Should().Contain("http://127.0.0.1:11476");
            resolved.Should().Contain("http://[::1]:11476");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 14) 字面 IP 端点原样保留为实际端点
    [Fact]
    public void Literal_ip_endpoint_is_kept_verbatim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://127.0.0.1:11476", "http://[::1]:11476" };
            var resolved = sd.GetActualListenerEndpoints();
            var expected = new[] { "http://127.0.0.1:11476", "http://[::1]:11476" };
            resolved.Should().BeEquivalentTo(expected);
        }
        finally { Directory.Delete(dir, true); }
    }

    // 15) 仅 localhost 输入 → 安全状态为 LOOPBACK_ONLY（localhost == 环回）
    [Fact]
    public void Localhost_only_resolves_to_LOOPBACK_ONLY()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://localhost:11476" };
            sd.GetListenerSecurityStatus().Should().Be("LOOPBACK_ONLY");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 16) 非环回字面端点 → NON_LOOPBACK_DETECTED
    [Fact]
    public void Non_loopback_literal_endpoint_is_NON_LOOPBACK_DETECTED()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string> { "http://10.0.0.5:11476" };
            sd.GetListenerSecurityStatus().Should().Be("NON_LOOPBACK_DETECTED");
        }
        finally { Directory.Delete(dir, true); }
    }

    // 17) 无端点 → UNKNOWN（不得臆造监听说明）
    [Fact]
    public void No_endpoints_resolves_to_UNKNOWN()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fso-sd-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var sd = NewDiagnostics(dir);
            sd.ActualBoundEndpoints = new List<string>();
            sd.GetActualListenerEndpoints().Should().BeEmpty();
            sd.GetListenerSecurityStatus().Should().Be("UNKNOWN");
        }
        finally { Directory.Delete(dir, true); }
    }
}
