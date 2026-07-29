using System.Diagnostics;
using System.IO.Pipes;
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
/// Picks a free loopback port (ADR-005 L2) for the Web Host's *HTTP UI* binding. The OS assigns an
/// ephemeral port bound to 127.0.0.1 only; the Host then listens on that port for the product web
/// UI. This is NOT the stop channel — control shutdown travels over a separate LOCAL Windows Named
/// Pipe (see <see cref="Launcher.StopPipeName"/>), which uses no TCP/HTTP/Socket/port.
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
/// and the data-directory lock, and never binds the network itself. Graceful shutdown is requested
/// over a LOCAL Windows Named Pipe (no TCP/HTTP/Socket/port) whose unguessable name + session token
/// are minted here and passed to the Host via <c>--stop-pipe</c> / <c>--stop-token</c>.
/// </summary>
public sealed class Launcher : IDisposable
{
    private const int GracefulStopTimeoutMs = 5000;
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
    /// One-time stop token (ADR-005 L3, M2-FIX-03 T13): high-entropy, session-scoped, passed to the
    /// Web Host via <c>--stop-token</c>. The Launcher presents it on the LOCAL Named Pipe stop
    /// channel (<c>STOP &lt;token&gt;</c>) to request a clean graceful shutdown. It is never logged
    /// or persisted (L9). This is SEPARATE from the bootstrap token so the bootstrap/session
    /// handshake stays single-use. Its lifetime is the whole session (the host may run for hours),
    /// so it is not a short 30s window.
    /// </summary>
    public string? StopToken { get; private set; }

    /// <summary>
    /// Windows local Named Pipe name for the stop channel (ADR-005 L2/L3, M2-FIX-03). The Launcher
    /// generates an UNPREDICTABLE name (session id + high-entropy random) and passes it to the Web
    /// Host via <c>--stop-pipe</c>. The pipe is a local-only IPC boundary — no TCP/HTTP/Socket/port
    /// is used for control. Combined with <see cref="StopToken"/>, the Web Host accepts a stop
    /// request only from this Launcher (pipe-name + token double binding, L3).
    /// </summary>
    public string? StopPipeName { get; private set; }

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

    /// <summary>Runs the launch: acquire lock, mint token + pipe, start Host, open browser, and on
    /// shutdown persist recovery state + terminate the Host (gracefully, over the Named Pipe).</summary>
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
        StopPipeName = MintStopPipeName(_session);

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
        string dotnetExe = Path.Combine(AppContext.BaseDirectory, "runtime", "dotnet", "dotnet.exe");
        string webDir = Path.Combine(AppContext.BaseDirectory, "web");
        string webDll = Path.Combine(webDir, "Observer.Host.Web.dll");

        if (!File.Exists(dotnetExe) || !File.Exists(webDll))
        {
            string expected = webDll;
            throw new FileNotFoundException(
                $"未找到 Observer Web Host（{webDll}）或包内运行时（{dotnetExe}）。发布包应在 CLI 安装目录下包含 " +
                $"web/Observer.Host.Web.dll 及 runtime/dotnet/dotnet.exe（预期位置：{expected}）。请重新发布完整包，不要手动复制文件。",
                "Observer.Host.Web");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetExe,
            // The stop channel travels over a LOCAL Windows Named Pipe: the Launcher mints an
            // unpredictable pipe name and a session token, and the Host binds only that pipe.
            // The Web Host's content root is pinned to <PackageRoot>/web via WorkingDirectory, so
            // static assets (web/wwwroot) resolve correctly regardless of the caller's cwd
            // (V030-RC-ENTRY-FIX-01 / DEFECT_3).
            Arguments = $"\"{webDll}\" --urls http://127.0.0.1:{Port} --bootstrap-token {BootstrapToken!.Value} --stop-pipe {StopPipeName!} --stop-token {StopToken!}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = webDir,
        };
        // V030-RC-ENTRY / RC3: expose the package-external release identity file to the Web Host via
        // an environment variable so SystemDiagnostics resolves EXTERNAL_RELEASE_IDENTITY
        // (observer_version / observer_commit / build_channel / release_status / package_sha256)
        // instead of falling back to DEVELOPMENT. The file is written by the build at
        // <PackageRoot>/release-identity.json. AppContext.BaseDirectory is the CLI's directory,
        // which equals the package root (observer.cmd launches the CLI dll from there), so the
        // child Web process always resolves the same absolute path regardless of the caller's cwd.
        startInfo.Environment["OBSERVER_RELEASE_IDENTITY_PATH"] =
            Path.Combine(AppContext.BaseDirectory, "release-identity.json");
        _hostProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Host 进程（Observer.Host.Web）。");
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

    /// <summary>
    /// Terminates the Host: requests a GRACEFUL shutdown over the local Named Pipe first, then only
    /// escalates to <c>Kill()</c> if the host fails to exit within <see cref="GracefulStopTimeoutMs"/>.
    /// A hard kill must never be the first action: an abrupt termination can abandon in-flight SQLite
    /// transactions and leave the store inconsistent (P0-B rule 2 expects in-flight tasks to reach
    /// RECOVERY_REQUIRED, which only happens if the host shuts down cleanly).
    /// </summary>
    private void TerminateHost()
    {
        if (_hostProcess is null)
        {
            return;
        }
        try
        {
            if (_hostProcess.HasExited)
            {
                Console.WriteLine("[Launcher] Host 已自行退出。");
                Console.WriteLine("GRACEFUL_EXIT=YES");
                Console.WriteLine("FORCED_KILL_FALLBACK=NO");
                return;
            }

            Console.WriteLine("[Launcher] 正在通过本地 Named Pipe 请求 Host 优雅退出…");
            bool stopRequested = RequestStopViaNamedPipe();
            if (!stopRequested)
            {
                // The windowless Web process has no message loop, so CloseMainWindow is a no-op;
                // it is only kept as a harmless best-effort fallback for any future GUI host.
                Console.WriteLine("[Launcher] stop pipe 不可达；回退到 CloseMainWindow（对无窗口 Web 进程通常无操作）。");
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
    /// Mints an unpredictable Named Pipe name for the stop channel (ADR-005 L2/L3, M2-FIX-03).
    /// The name folds in the per-launch session id and a fresh 16-byte random so it cannot be
    /// enumerated or guessed by another local principal. The pipe is a LOCAL Windows IPC boundary
    /// only — no TCP/HTTP/Socket/port is involved.
    /// </summary>
    private static string MintStopPipeName(string session)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        string random = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"fso-observer-stop-{session}-{random}";
    }

    /// <summary>
    /// Requests a graceful shutdown over the LOCAL Windows Named Pipe the Web Host is listening on
    /// (ADR-005 L2/L3, M2-FIX-03, M2-FIX-04). Sends <c>STOP &lt;session-token&gt;</c> and reads the
    /// single-line response (<c>ACK</c> = accepted, host stopping; <c>REJECT</c> = bad token). Returns
    /// true if the pipe boundary was reached (so we then wait for a clean exit), false on any
    /// transport failure. This is the sole cross-process control signal and uses NO network client.
    /// <para>
    /// The client connects with <see cref="PipeOptions.CurrentUserOnly"/>, matching the server. On
    /// Windows the OS restricts the pipe to the SAME user AND the SAME elevation level that created
    /// it, so NO token impersonation is requested — the same-user boundary is enforced by the kernel
    /// and does NOT depend on the <c>SeImpersonatePrivilege</c> the sandbox may lack (M2-FIX-04).
    /// </para>
    /// </summary>
    private bool RequestStopViaNamedPipe()
    {
        if (_hostProcess is null || string.IsNullOrEmpty(StopPipeName) || string.IsNullOrEmpty(StopToken))
        {
            return false;
        }
        try
        {
            // M2-FIX-04: connect with PipeOptions.CurrentUserOnly (no token impersonation). The OS
            // limits the pipe to the same Windows user + elevation level, which is all we need — no
            // impersonation level and no SeImpersonatePrivilege dependency.
            using var pipeClient = new NamedPipeClientStream(
                ".",
                StopPipeName,
                PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            pipeClient.Connect(2000);

            using var writer = new StreamWriter(pipeClient, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipeClient, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            writer.WriteLine($"STOP {StopToken}");

            string? response = reader.ReadLine();
            // Either ACK (host will stop) or REJECT (bad token/user) means the local pipe boundary
            // was exercised; we treat it as "channel ok" and let WaitForExit decide the outcome.
            return response is not null && (response == "ACK" || response == "REJECT");
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
