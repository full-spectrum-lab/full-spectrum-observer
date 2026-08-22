using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using FluentAssertions;
using FullSpectrum.Observer.Host.Cli;
using FullSpectrum.Observer.Host.Web.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class LauncherShutdownTests
{
    [Fact]
    public void Launch_settings_prioritize_command_line_then_environment()
    {
        LaunchSettings.ResolveDataDirectoryOverride("C:\\package", "C:\\cli", "C:\\environment")
            .Should().Be("C:\\cli");
        LaunchSettings.ResolveDataDirectoryOverride("C:\\package", null, "C:\\environment")
            .Should().Be("C:\\environment");
    }

    [Fact]
    public void Launch_settings_find_strict_parent_sidecar_for_literal_double_click()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"observer-launch-{Guid.NewGuid():N}");
        string root = Path.Combine(parent, "观察者 候选包");
        string dataDirectory = Path.Combine(parent, "隔离 数据");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(parent, LaunchSettings.FileName),
                $$"""{"data_directory":"{{dataDirectory.Replace("\\", "\\\\")}}"}""");

            LaunchSettings.ResolveDataDirectoryOverride(root, null, null).Should().Be(dataDirectory);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Launch_settings_reject_unknown_fields()
    {
        string root = Path.Combine(Path.GetTempPath(), $"observer-launch-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, LaunchSettings.FileName),
                "{\"data_directory\":\"C:\\\\isolated\",\"unknown\":true}");

            Action act = () => LaunchSettings.ResolveDataDirectoryOverride(root, null, null);
            act.Should().Throw<InvalidDataException>().WithMessage("*只允许 data_directory*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Launch_settings_reject_relative_data_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"observer-launch-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, LaunchSettings.FileName),
                "{\"data_directory\":\"relative-data\"}");

            Action act = () => LaunchSettings.ResolveDataDirectoryOverride(root, null, null);
            act.Should().Throw<InvalidDataException>().WithMessage("*必须是非空绝对路径*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void External_identity_discovery_prefers_explicit_configuration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"observer-package-{Guid.NewGuid():N}");
        string explicitPath = Path.Combine(Path.GetTempPath(), $"observer-identity-{Guid.NewGuid():N}.json");

        Launcher.ResolveExternalIdentityPath(root, explicitPath).Should().Be(Path.GetFullPath(explicitPath));
    }

    [Fact]
    public void External_identity_discovery_finds_public_asset_beside_extracted_directory()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"observer-download-{Guid.NewGuid():N}");
        string root = Path.Combine(parent, "观察者 候选包");
        string identity = Path.Combine(parent, "observer_IDENTITY.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(identity, "{}");

            Launcher.ResolveExternalIdentityPath(root, null).Should().Be(identity);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void External_identity_discovery_does_not_mislabel_internal_release_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), $"observer-package-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "release-identity.json"), "{}");

            Launcher.ResolveExternalIdentityPath(root, null).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void Windows_console_termination_events_are_handled(uint controlType)
    {
        WindowsConsoleShutdown.IsWindowTerminationEvent(controlType).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Other_console_events_are_not_claimed(uint controlType)
    {
        WindowsConsoleShutdown.IsWindowTerminationEvent(controlType).Should().BeFalse();
    }

    [Fact]
    public void Parent_identity_probe_accepts_the_current_process()
    {
        using var current = Process.GetCurrentProcess();

        LauncherLifetimeMonitor.IsExpectedProcessAlive(
            current.Id,
            current.StartTime.ToUniversalTime().Ticks).Should().BeTrue();
    }

    [Fact]
    public void Parent_identity_probe_rejects_a_reused_pid_identity()
    {
        using var current = Process.GetCurrentProcess();

        LauncherLifetimeMonitor.IsExpectedProcessAlive(
            current.Id,
            current.StartTime.ToUniversalTime().Ticks + 1).Should().BeFalse();
    }

    [Fact]
    public void Parent_identity_probe_rejects_an_invalid_pid()
    {
        LauncherLifetimeMonitor.IsExpectedProcessAlive(-1, 1).Should().BeFalse();
    }

    [Fact]
    public async Task Stop_channel_rearms_after_a_client_disconnects_before_sending_a_request()
    {
        string pipeName = $"observer-stop-test-{Guid.NewGuid():N}";
        const string stopToken = "valid-stop-token";
        using var lifetime = new TestHostApplicationLifetime();
        using var channel = new NamedPipeStopChannel(lifetime, pipeName, stopToken);
        await channel.StartAsync(CancellationToken.None);

        using (var abandonedClient = CreateClient(pipeName))
        {
            await abandonedClient.ConnectAsync(5_000);
        }

        using var validClient = CreateClient(pipeName);
        await validClient.ConnectAsync(5_000);
        using var writer = new StreamWriter(validClient, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(validClient, Encoding.ASCII, leaveOpen: true);

        await writer.WriteLineAsync($"STOP {stopToken}");

        (await reader.ReadLineAsync()).Should().Be("ACK");
        await lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopCount.Should().Be(1);
        await channel.StopAsync(CancellationToken.None);
    }

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public Task Stopped => _stopped.Task;

        public int StopCount { get; private set; }

        public void StopApplication()
        {
            StopCount++;
            _stopping.Cancel();
            _stopped.TrySetResult();
        }

        public void Dispose() => _stopping.Dispose();
    }
}
