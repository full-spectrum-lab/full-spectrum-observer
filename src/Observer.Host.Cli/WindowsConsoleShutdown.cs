using System.Runtime.InteropServices;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Converts Windows console-window close/logoff/shutdown events into the same cancellation path
/// used by Ctrl+C. The native callback waits for the managed launcher cleanup to finish before it
/// returns, because Windows terminates the console process after the close handler returns.
/// </summary>
internal sealed class WindowsConsoleShutdown : IDisposable
{
    private const uint CtrlCloseEvent = 2;
    private const uint CtrlLogoffEvent = 5;
    private const uint CtrlShutdownEvent = 6;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(12);

    private readonly CancellationTokenSource _shutdown;
    private readonly ManualResetEventSlim _cleanupComplete;
    private readonly ConsoleCtrlHandler _handler;
    private bool _registered;

    private WindowsConsoleShutdown(
        CancellationTokenSource shutdown,
        ManualResetEventSlim cleanupComplete)
    {
        _shutdown = shutdown;
        _cleanupComplete = cleanupComplete;
        _handler = HandleConsoleControl;

        if (OperatingSystem.IsWindows())
        {
            _registered = SetConsoleCtrlHandler(_handler, add: true);
        }
    }

    public static WindowsConsoleShutdown Register(
        CancellationTokenSource shutdown,
        ManualResetEventSlim cleanupComplete) =>
        new(shutdown, cleanupComplete);

    internal static bool IsWindowTerminationEvent(uint controlType) =>
        controlType is CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent;

    private bool HandleConsoleControl(uint controlType)
    {
        if (!IsWindowTerminationEvent(controlType))
        {
            return false;
        }

        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _cleanupComplete.Wait(CleanupTimeout);
        return true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            SetConsoleCtrlHandler(_handler, add: false);
            _registered = false;
        }
    }

    private delegate bool ConsoleCtrlHandler(uint controlType);

    [DllImport("Kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
