using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Holds the one-time stop token the Launcher minted and passed to the Host via
/// <c>--stop-token</c> (ADR-005 L3). The Host validates it in constant time (same seam as
/// <see cref="BootstrapTokenContext"/>) and enforces a generous session-scoped lifetime so a
/// long-running console can always shut down cleanly. The token is NEVER written to logs,
/// diagnostics, or the database (L9/L16).
/// </summary>
public sealed class StopTokenContext
{
    public string? Token { get; }

    public DateTimeOffset IssuedAt { get; }

    public TimeSpan Lifetime { get; }

    public StopTokenContext(string? token, TimeSpan lifetime)
    {
        Token = token;
        IssuedAt = DateTimeOffset.UtcNow;
        Lifetime = lifetime;
    }

    public bool IsExpired(DateTimeOffset now) => now - IssuedAt > Lifetime;

    /// <summary>Constant-time comparison so the token never leaks via timing (L9).</summary>
    public static bool ConstantTimeEquals(string? a, string? b) =>
        BootstrapTokenContext.ConstantTimeEquals(a, b);
}

/// <summary>
/// Internal, LOCAL Windows Named Pipe stop channel (ADR-005 L2/L3, M2-FIX-03 T11, revised).
///
/// The Launcher mints an UNPREDICTABLE pipe name (passed via <c>--stop-pipe</c>) and a one-time
/// session token (passed via <c>--stop-token</c>). This hosted service listens on that pipe — a
/// local-only IPC boundary using <see cref="NamedPipeServerStream"/> (System.IO.Pipes). NO
/// TCP/HTTP/Socket/port is used for control.
///
/// Protocol (minimal): the client sends a single line <c>STOP &lt;session-token&gt;</c>; the server
/// replies <c>ACK</c> (accepted) or <c>REJECT</c> (bad token / wrong user).
///
/// Security boundaries:
/// <list type="bullet">
///   <item><description>The pipe name is unguessable (session id + 16-byte random), so another local
///     principal cannot even locate the control endpoint.</description></item>
///   <item><description>Before processing a request, the server impersonates the client
///     (<see cref="NamedPipeServerStream.RunAsClient"/>) and verifies the caller is the SAME Windows
///     principal that started this host (the Launcher launches this host as a child process, so they
///     share the user). A different local user is rejected.</description></item>
///   <item><description>The session token is validated in constant time and is not expired for the
///     whole session, so only THIS Launcher's token stops THIS host (pipe-name + token double
///     binding, L3).</description></item>
/// </list>
///
/// On <c>ACK</c> the server writes the response FIRST and then calls
/// <see cref="IHostApplicationLifetime.StopApplication"/>, which triggers a clean graceful shutdown
/// (ApplicationStopping -> AnalysisShutdownToken.Signal() -> Engine worker cancellation -> clean
/// exit). On <c>REJECT</c> it disconnects. The pipe is always disposed on host shutdown so no pipe,
/// worker, or SQLite lock is leaked (constraint 10).
/// </summary>
public sealed class NamedPipeStopChannel : IHostedService, IDisposable
{
    private const string RequestPrefix = "STOP ";
    private const string ResponseAck = "ACK";
    private const string ResponseReject = "REJECT";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _pipeName;
    private readonly StopTokenContext _tokenContext;
    private readonly string _serverUser;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;
    private bool _disposed;

    public NamedPipeStopChannel(IHostApplicationLifetime lifetime, string pipeName, string stopToken)
    {
        _lifetime = lifetime;
        _pipeName = pipeName;
        // Session-scoped lifetime: the console may run for hours, so the stop token must stay valid
        // for the whole session (not a short 30s window). Pipe-name + token double-binding is the
        // real control boundary; expiry is defense-in-depth against a leaked token.
        _tokenContext = new StopTokenContext(stopToken, TimeSpan.FromHours(24));
        _serverUser = WindowsIdentity.GetCurrent().Name;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Start the listen loop on a background thread; the host continues booting (app.Run blocks
        // the calling thread). The loop ends when StopApplication is triggered or the host stops.
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel the loop (unblocks WaitForConnectionAsync) and release the pipe so no handle leaks.
        try { _cts.Cancel(); } catch { /* already disposed */ }
        try { _pipe?.Dispose(); } catch { /* already disposed */ }
        return Task.CompletedTask;
    }

    private Task? _listenTask;

    private async Task ListenLoopAsync(CancellationToken token)
    {
        try
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 1024,
                outBufferSize: 1024);
        }
        catch (Exception ex)
        {
            // Cannot create the control pipe (e.g. name clash) — degrade gracefully: no stop channel.
            Console.WriteLine($"[StopChannel] 无法创建 Named Pipe 停止通道（{_pipeName}）：{ex.Message}");
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                // L5: restrict to the same Windows principal that started this host.
                if (!IsSameWindowsUser(_pipe))
                {
                    await WriteLineAsync(_pipe, ResponseReject, token).ConfigureAwait(false);
                    _pipe.Disconnect();
                    continue;
                }

                string response = await ReadAndValidateAsync(_pipe, token).ConfigureAwait(false);
                await WriteLineAsync(_pipe, response, token).ConfigureAwait(false);

                if (response == ResponseAck)
                {
                    // Triggers IHostApplicationLifetime.ApplicationStopping -> analysis cancellation
                    // -> clean exit. The ACK was already flushed above, before StopApplication.
                    _lifetime.StopApplication();
                    return;
                }

                _pipe.Disconnect();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Pipe closed, client disconnected, or host shutting down. Stop serving.
                try { _pipe.Disconnect(); } catch { /* no-op */ }
                break;
            }
        }
    }

    /// <summary>Impersonates the connected client and verifies it is the same Windows user that
    /// started this host. Returns false (reject) on any impersonation failure.</summary>
    private bool IsSameWindowsUser(NamedPipeServerStream pipe)
    {
        try
        {
            string? clientUser = null;
            pipe.RunAsClient(() =>
            {
                clientUser = WindowsIdentity.GetCurrent().Name;
            });
            return string.Equals(clientUser, _serverUser, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the single request line and validates the STOP + session token. Returns
    /// <c>ACK</c> or <c>REJECT</c> (does not write the response).</summary>
    private async Task<string> ReadAndValidateAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null)
        {
            return ResponseReject;
        }

        string request = line.TrimEnd('\r');
        if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal))
        {
            return ResponseReject;
        }

        string presented = request.Substring(RequestPrefix.Length);
        bool ok = StopTokenContext.ConstantTimeEquals(presented, _tokenContext.Token)
                  && !_tokenContext.IsExpired(DateTimeOffset.UtcNow);
        return ok ? ResponseAck : ResponseReject;
    }

    private static async Task WriteLineAsync(NamedPipeServerStream pipe, string message, CancellationToken token)
    {
        using var writer = new StreamWriter(pipe, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _cts.Cancel(); } catch { /* no-op */ }
        try { _cts.Dispose(); } catch { /* no-op */ }
        try { _pipe?.Dispose(); } catch { /* no-op */ }
    }
}
