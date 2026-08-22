using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Defense in depth for abrupt launcher termination. The normal path is the authenticated stop
/// pipe; if Windows terminates the launcher before managed cleanup runs, the Web host observes that
/// its exact parent process identity disappeared and stops itself instead of becoming orphaned.
/// </summary>
public sealed class LauncherLifetimeMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly int _launcherPid;
    private readonly long _launcherStartUtcTicks;

    public LauncherLifetimeMonitor(
        IHostApplicationLifetime lifetime,
        int launcherPid,
        long launcherStartUtcTicks)
    {
        _lifetime = lifetime;
        _launcherPid = launcherPid;
        _launcherStartUtcTicks = launcherStartUtcTicks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsExpectedProcessAlive(_launcherPid, _launcherStartUtcTicks))
            {
                _lifetime.StopApplication();
                return;
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public static bool IsExpectedProcessAlive(int pid, long expectedStartUtcTicks)
    {
        if (pid <= 0 || expectedStartUtcTicks <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited &&
                   process.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
