using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Execution;

namespace FullSpectrum.Observer.Host.Cli;

public static class ObserverHostFactory
{
    public static HostComponents Create(string dataDirectory, string allowedInputRoot)
    {
        // M2-FIX-03: resolve every runtime path from the package root via the shared resolver.
        // The resolver derives PackageRoot from AppContext.BaseDirectory (the host's own assembly
        // directory), so this works from any working directory — including a movable published
        // package launched from an unrelated cwd — without reading FSP_PRIVATE_PYTHON except as a
        // test/diagnostic escape hatch.
        var config = FullSpectrum.Observer.Contracts.RuntimeConfigurationResolver.Resolve();
        string root = config.PackageRoot;
        string schemaDirectory = config.SchemaDirectory;
        string packDirectory = config.CasePackDirectory;
        string worker = config.WorkerScriptPath;
        string engineRoot = config.EngineRootPath;
        string workerLock = config.WorkerLockPath;
        string python = config.PythonExecutablePath;

        var clock = new SystemClock();
        var ids = new GuidIdGenerator();
        EvidenceComponents evidence = EvidenceComposition.Create(new EvidenceOptions
        {
            DataDirectory = Path.GetFullPath(dataDirectory),
        }, clock, ids);

        // `python` is always an absolute path (the resolver returns either the FSP_PRIVATE_PYTHON
        // override or <PackageRoot>/runtime/python/python.exe), so the only validity check is that
        // the interpreter and the Engine artifacts actually exist on disk.
        bool engineReady = File.Exists(python)
            && File.Exists(worker)
            && File.Exists(workerLock)
            && Directory.Exists(engineRoot);

        IObserverEngineFacade facade = engineReady
            ? EngineFacadeComposition.Create(new EngineFacadeOptions
            {
                PythonExecutablePath = Path.GetFullPath(python!),
                WorkerScriptPath = Path.GetFullPath(worker),
                EngineRootPath = Path.GetFullPath(engineRoot),
                WorkerLockPath = Path.GetFullPath(workerLock),
                SchemaDirectory = Path.GetFullPath(schemaDirectory),
            })
            : new UnavailableEngineFacade();

        var executionOptions = new FoundationExecutionOptions
        {
            RepositoryRoot = Path.GetFullPath(root),
            SchemaDirectory = Path.GetFullPath(schemaDirectory),
            CasePackDirectory = Path.GetFullPath(packDirectory),
            AllowedInputRoot = Path.GetFullPath(allowedInputRoot),
            DataDirectory = Path.GetFullPath(dataDirectory),
        };
        var port = new EvidenceComponentsPort(
            evidence.Session,
            evidence.Operations,
            evidence.Observations,
            evidence.RuntimeSnapshots,
            evidence.Audit,
            evidence.Artifacts,
            evidence.Idempotency);
        ExecutionUseCases useCases = ExecutionComposition.Create(
            executionOptions,
            port,
            facade,
            clock,
            ids,
            () => engineReady);
        return new HostComponents(useCases, evidence.Session, clock, ids);
    }
}

public sealed record HostComponents(
    ExecutionUseCases UseCases,
    IEvidenceSession EvidenceSession,
    IClock Clock,
    IIdGenerator Ids) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => EvidenceSession.DisposeAsync();
}
