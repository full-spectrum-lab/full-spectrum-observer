using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
            Arguments = $"--urls http://127.0.0.1:{Port} --bootstrap-token {BootstrapToken!.Value}",
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
            // request a graceful shutdown first and only escalate to Kill() if the window elapses.
            if (_hostProcess.HasExited)
            {
                Console.WriteLine("[Launcher] Host 已自行退出。");
                Console.WriteLine("GRACEFUL_EXIT=YES");
                Console.WriteLine("FORCED_KILL_FALLBACK=NO");
                return;
            }

            Console.WriteLine("[Launcher] 正在请求 Host 优雅退出…");
            bool requested = _hostProcess.CloseMainWindow();
            if (!requested)
            {
                // Windowless console host: there is no message loop to pump a WM_CLOSE, so
                // CloseMainWindow cannot deliver the signal. We still grant the host a chance to
                // self-terminate, then fall back to a hard kill below as a last resort.
                Console.WriteLine("[Launcher] Host 无主窗口，无法投递窗口关闭消息；进入优雅退出等待窗口。");
            }

            if (!_hostProcess.WaitForExit(GracefulStopTimeoutMs))
            {
                Console.WriteLine("[Launcher] Host 未在优雅退出窗口内结束，执行强制终止（最后手段）。");
                Console.WriteLine("GRACEFUL_EXIT=NO");
                Console.WriteLine("FORCED_KILL_FALLBACK=YES");
                _hostProcess.Kill();
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

    public void Dispose()
    {
        _instanceLock.Dispose();
        _hostProcess?.Dispose();
    }
}
