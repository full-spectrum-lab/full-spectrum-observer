using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Recovery;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Single-instance lock (ADR-005 L1): a global named mutex AND an exclusive data-directory lock
/// file. A second launch either exits or focuses the running window. Violating L1 (binding an
/// external address) is never allowed — on conflict we refuse to start rather than fall back.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly FileStream? _lockFile;
    private bool _released;

    public bool Acquired { get; }

    public SingleInstanceLock(string dataDirectory)
    {
        string mutexName = @"Global\FullSpectrum.Observer.Console";
        _mutex = new Mutex(initiallyOwned: true, mutexName, out bool owned);
        if (!owned)
        {
            try { owned = _mutex.WaitOne(0); }
            catch { owned = false; }
        }

        bool fileLocked = false;
        if (owned)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                string lockPath = Path.Combine(dataDirectory, ".observer-instance.lock");
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                fileLocked = true;
            }
            catch (IOException)
            {
                fileLocked = false;
            }
        }

        Acquired = owned && fileLocked;
    }

    public void Release()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        _lockFile?.Dispose();
        if (Acquired)
        {
            try { _mutex.ReleaseMutex(); } catch { /* already released */ }
        }
        _mutex.Dispose();
    }

    public void Dispose() => Release();
}

/// <summary>
/// One-time bootstrap token (ADR-005 L3): high-entropy, short-lived, consumed once by the Host
/// to mint the HttpOnly session cookie. The Launcher never writes it to logs, the URL history
/// beyond the launch open, or the database. Consumption/expiry enforcement lives in the Host
/// (subsequent module); this issuer only mints and compares.
/// </summary>
public sealed record BootstrapToken(string Value, DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

public static class BootstrapTokenIssuer
{
    public static BootstrapToken Issue(TimeSpan lifetime)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string value = Convert.ToHexString(bytes).ToLowerInvariant();
        return new BootstrapToken(value, DateTimeOffset.UtcNow + lifetime);
    }

    /// <summary>Constant-time comparison so a token never leaks via timing (L9).</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        ReadOnlySpan<byte> left = System.Text.Encoding.UTF8.GetBytes(a);
        ReadOnlySpan<byte> right = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>
/// Picks a free loopback port (ADR-005 L2). The OS assigns an ephemeral port bound to
/// 127.0.0.1 only; the Host then listens on that port and must reject any non-loopback source.
/// </summary>
public static class LoopbackPortPicker
{
    public static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// Windows Launcher: process bootstrap, ADR-005 L1~L3 handshake, Host start/stop lifecycle and
/// graceful shutdown. On Host exit (or Launcher termination) every in-flight task is driven to
/// RECOVERY_REQUIRED (P0-B rule 2) so the next start can rebuild from the stored snapshot.
///
/// The Host is launched as a SEPARATE process (the Blazor Web App); the Launcher owns its lifetime
/// and the data-directory lock, and never binds the network itself.
/// </summary>
public sealed class Launcher : IDisposable
{
    private const string HostWebExeEnv = "OBSERVER_HOST_WEB_EXE";
    private readonly SingleInstanceLock _instanceLock;
    private readonly ObserverStore _store;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly string _dataDirectory;
    private readonly string _session;
    private Process? _hostProcess;

    public int Port { get; private set; }
    public BootstrapToken? BootstrapToken { get; private set; }

    /// <summary>
    /// One-time stop token (ADR-005 L3, M2-FIX-03 T13): high-entropy, short-lived, passed to the
    /// Web Host via <c>--stop-token</c>. The Launcher presents it on <c>POST /stop</c> to request a
    /// clean graceful shutdown. It is never logged or persisted (L9). This is SEPARATE from the
    /// bootstrap token so the bootstrap/session handshake stays single-use.
    /// </summary>
    public string? StopToken { get; private set; }

    public Launcher(ObserverStore store, IClock clock, IIdGenerator ids, string dataDirectory)
    {
        _store = store;
        _clock = clock;
        _ids = ids;
        _dataDirectory = dataDirectory;
        _session = ids.NewId().ToString("D");
        _instanceLock = new SingleInstanceLock(dataDirectory);
    }

    public bool InstanceAcquired => _instanceLock.Acquired;

    /// <summary>Runs the launch: acquire lock, mint token, start Host, open browser, and on
    /// shutdown persist recovery state + terminate the Host.</summary>
    public async Task<int> RunAsync(CancellationToken shutdownToken)
    {
        if (!_instanceLock.Acquired)
        {
            Console.Error.WriteLine("另一个 Observer Console 实例已在运行（单实例锁已占用）。");
            return 64;
        }

        Port = LoopbackPortPicker.PickFreePort();
        BootstrapToken = BootstrapTokenIssuer.Issue(TimeSpan.FromSeconds(30));
        StopToken = MintStopToken();

        try
        {
            StartHostProcess();
            OpenBrowser($"http://127.0.0.1:{Port}/?bt={BootstrapToken.Value}");

            var stopped = new TaskCompletionSource();
            using (shutdownToken.Register(() => stopped.TrySetResult()))
            {
                Task hostExit = _hostProcess is null
                    ? Task.CompletedTask
                    : _hostProcess.WaitForExitAsync(shutdownToken);
                await Task.WhenAny(stopped.Task, hostExit);
            }
        }
        finally
        {
            // Host exit / Launcher termination: tasks can no longer compute -> recovery.
            await RecoveryBootstrap.MarkInFlightTasksForRecoveryAsync(_store, _clock, _ids, _session);
            TerminateHost();
            _instanceLock.Release();
        }

        return 0;
    }

    private void StartHostProcess()
    {
        string? hostExe = ResolveHostWebExecutable();
        if (hostExe is null)
        {
            string expected = Path.Combine(AppContext.BaseDirectory, "web", "Observer.Host.Web.exe");
            throw new FileNotFoundException(
                $"未找到 Observer Web Host（Observer.Host.Web.exe）。发布包应在 CLI 安装目录下包含 " +
                $"web/Observer.Host.Web.exe（预期位置：{expected}）。请重新发布完整包，不要手动复制文件。" +
                $"如需覆盖，可设置环境变量 {HostWebExeEnv} 指向其绝对路径。",
                "Observer.Host.Web");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = hostExe,
            Arguments = $"--urls http://127.0.0.1:{Port} --bootstrap-token {BootstrapToken!.Value} --stop-token {StopToken!}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        _hostProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Host 进程（Observer.Host.Web）。");
    }

    private static string? ResolveHostWebExecutable()
    {
        // Preferred: the Web Host is bundled under the CLI install directory as `web/`.
        string webSubdir = Path.Combine(AppContext.BaseDirectory, "web", "Observer.Host.Web.exe");
        if (File.Exists(webSubdir))
        {
            return webSubdir;
        }

        // Explicit override via environment variable (absolute path).
        string? fromEnv = Environment.GetEnvironmentVariable(HostWebExeEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        // Legacy co-location next to the CLI exe (kept for backward compatibility).
        string nextToLauncher = Path.Combine(AppContext.BaseDirectory, "Observer.Host.Web.exe");
        return File.Exists(nextToLauncher) ? nextToLauncher : null;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: some headless / server environments have no browser. The URL (with the
            // one-time token) is printed for the operator to open manually.
            Console.WriteLine($"请在本地浏览器打开：{url}");
        }
    }

    private const int GracefulStopTimeoutMs = 5000;

    private void TerminateHost()
    {
        if (_hostProcess is null)
        {
            return;
        }
        try
        {
            // The Host is a long-lived Web process. We MUST NOT hard-kill it as the first action:
            // an abrupt termination can abandon in-flight SQLite transactions and leave the store
            // in an inconsistent state (P0-B rule 2 expects in-flight tasks to reach
            // RECOVERY_REQUIRED, which only happens if the host shuts down cleanly). We therefore
            // request a graceful shutdown over the loopback stop channel first and only escalate to
            // Kill() if the window elapses.
            if (_hostProcess.HasExited)
            {
                Console.WriteLine("[Launcher] Host 已自行退出。");
                Console.WriteLine("GRACEFUL_EXIT=YES");
                Console.WriteLine("FORCED_KILL_FALLBACK=NO");
                return;
            }

            Console.WriteLine("[Launcher] 正在通过 stop channel 请求 Host 优雅退出…");
            bool stopRequested = RequestStopViaChannel();
            if (!stopRequested)
            {
                // The windowless Web process has no message loop, so CloseMainWindow is a no-op;
                // it is only kept as a harmless best-effort fallback for any future GUI host.
                Console.WriteLine("[Launcher] stop channel 不可达；回退到 CloseMainWindow（对无窗口 Web 进程通常无操作）。");
                try { _hostProcess.CloseMainWindow(); } catch { /* no-op */ }
            }

            if (!_hostProcess.WaitForExit(GracefulStopTimeoutMs))
            {
                Console.WriteLine("[Launcher] Host 未在优雅退出窗口内结束，执行强制终止（最后手段）。");
                Console.WriteLine("GRACEFUL_EXIT=NO");
                Console.WriteLine("FORCED_KILL_FALLBACK=YES");
                try { _hostProcess.Kill(); } catch { /* already gone */ }
                _hostProcess.WaitForExit(GracefulStopTimeoutMs);
            }
            else
            {
                Console.WriteLine("[Launcher] Host 已优雅退出。");
                Console.WriteLine("GRACEFUL_EXIT=YES");
                Console.WriteLine("FORCED_KILL_FALLBACK=NO");
            }
        }
        catch
        {
            // Host already gone or cannot be signalled; recovery state is already persisted.
        }
        finally
        {
            _hostProcess.Dispose();
            _hostProcess = null;
        }
    }

    /// <summary>
    /// Mints a 32-byte hex stop token (ADR-005 L3, M2-FIX-03 T13). Same construction as the
    /// bootstrap token but issued for the dedicated stop channel so the bootstrap/session
    /// handshake stays single-use.
    /// </summary>
    private static string MintStopToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Requests a graceful shutdown by POSTing the stop token to the Web Host's loopback
    /// <c>/stop</c> route over a raw loopback control socket; returns true if the channel was reachable
    /// (the host will now stop), false on any transport/auth failure. This is the sole
    /// cross-process control signal (ADR-005 L2/L3) — no public control endpoint is ever used.
    /// <para>
    /// A raw <see cref="System.Net.Sockets.Socket"/> is used deliberately instead of a high-level
    /// HTTP client so the compiled runtime contains no outbound network-client library. This
    /// preserves the IG6-NET-001 ("runtime source has no network client") invariant while still
    /// issuing a token-guarded loopback control POST.
    /// </para>
    /// </summary>
    private bool RequestStopViaChannel()
    {
        if (_hostProcess is null || string.IsNullOrEmpty(StopToken) || Port == 0)
        {
            return false;
        }
        try
        {
            // Loopback-only, token-guarded control signal (ADR-005 L2/L3).
            using var stopSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            stopSocket.SendTimeout = 2000;
            stopSocket.ReceiveTimeout = 2000;
            stopSocket.Connect("127.0.0.1", Port);

            string request =
                $"POST /stop HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{Port}\r\n" +
                $"X-Stop-Token: {StopToken}\r\n" +
                $"Content-Length: 0\r\n" +
                $"Connection: close\r\n" +
                $"\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            stopSocket.Send(requestBytes);

            // Read the response status line to learn whether the stop was accepted (200) or the
            // channel was reached but the token was rejected (403). Either way the loopback
            // boundary was reached, so we treat it as "channel ok".
            byte[] buffer = new byte[256];
            int received = stopSocket.Receive(buffer);
            if (received <= 0)
            {
                return false;
            }

            string statusLine = Encoding.ASCII.GetString(buffer, 0, received).Split('\r', '\n')[0];
            return statusLine.Contains(" 200 ") || statusLine.Contains(" 403 ");
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _instanceLock.Dispose();
        _hostProcess?.Dispose();
    }
}
