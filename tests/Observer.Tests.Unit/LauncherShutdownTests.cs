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
