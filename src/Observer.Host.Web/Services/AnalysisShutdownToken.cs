using System.Threading;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// A single cancellation source that is signalled when the host begins stopping
/// (<c>IHostApplicationLifetime.ApplicationStopping</c>). In-flight analysis operations take this
/// token, so cancelling the host cleanly cancels the Engine worker process via the existing
/// EngineFacade cancel path (M2-FIX-03, T12). No forced <c>Kill</c> is required for a controlled
/// shutdown.
/// </summary>
public sealed class AnalysisShutdownToken
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>The token that analysis operations should observe.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Signal cancellation (idempotent).</summary>
    public void Signal() => _source.Cancel();
}
