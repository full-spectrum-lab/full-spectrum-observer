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
        string root = RepositoryLayout.FindRoot();
        string schemaDirectory = RepositoryLayout.SchemaDirectory(root);
        string packDirectory = Path.Combine(root, "packs", "foundation-case005");
        string worker = Path.Combine(root, "engine", "worker", "worker.py");
        string engineRoot = Path.Combine(root, "engine", "vendor", "full-spectrum-engine");
        string workerLock = Path.Combine(root, "engine", "worker.lock.json");
        string? python = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON");

        var clock = new SystemClock();
        var ids = new GuidIdGenerator();
        EvidenceComponents evidence = EvidenceComposition.Create(new EvidenceOptions
        {
            DataDirectory = Path.GetFullPath(dataDirectory),
        }, clock, ids);

        bool engineReady = !string.IsNullOrWhiteSpace(python)
            && Path.IsPathFullyQualified(python)
            && File.Exists(python)
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
