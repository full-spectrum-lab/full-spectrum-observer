using System.Diagnostics;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// The single canonical boundary that issues <see cref="Process.Start()"/> for the pinned
/// Engine v1.5.0 worker process. Both the CLI facade (<see cref="PythonWorkerEngineFacade"/>)
/// and the Web facade (<see cref="EngineFacade"/>) MUST route worker process startup through
/// this class so the "single Worker process start boundary" architectural invariant
/// (IG6-ARCH-001) is preserved. Each caller keeps its own fail-closed exception type; this class
/// only owns the one physical <c>Process.Start()</c> call site.
/// </summary>
public static class WorkerProcessHost
{
    /// <summary>
    /// Starts the already-configured worker <see cref="Process"/>. Returns the result of
    /// <see cref="Process.Start()"/> so each facade can translate a <c>false</c> into its own
    /// domain exception.
    /// </summary>
    public static bool Start(Process process) => process.Start();
}
