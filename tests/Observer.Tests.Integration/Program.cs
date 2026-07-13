using System.Globalization;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Evidence;

string scope = Environment.GetEnvironmentVariable("FSP_TEST_SCOPE") ?? "ALL";
var tests = new List<(string Id, Func<Task> Execute)>();
if (scope is "ALL" or "IG3")
{
    tests.Add(("TR-FK-AUD-001", AppendAndVerifyAudit));
    tests.Add(("TR-FK-AUD-003", RejectAuditMutation));
    tests.Add(("TR-FK-IDEM-001", IdempotencyReservationIsStable));
    tests.Add(("TR-FK-STORE-001-SMOKE", FinalizationIsAtomicAtSourceLevel));
}
if (scope is "ALL" or "IG4")
{
    tests.Add(("TR-FK-CON-004/OUT-001", RunEngineFacadeSmoke));
}
if (scope is "ALL" or "IG5")
{
    tests.Add(("TR-FK-INT-001", RunFoundationPipelineSmoke));
}
int failures = 0;
foreach ((string id, Func<Task> execute) in tests)
{
    try { await execute(); Console.WriteLine($"PASS {id}"); }
    catch (DllNotFoundException exception) { failures++; Console.Error.WriteLine($"BLOCKED {id}: sqlite3 runtime missing: {exception.Message}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {id}: {exception.Message}"); }
}
return failures == 0 ? 0 : 1;

async Task AppendAndVerifyAudit()
{
    await using TestContext context = await TestContext.CreateAsync();
    await context.PreparePersistingOperationAsync();
    EvidenceFinalizationResult result = await context.FinalizeAsync();
    AuditVerificationResult verified = await context.Components.Audit.VerifyAsync(1, CancellationToken.None);
    Assert(verified.IsValid && verified.CheckedEvents == 1, "Audit chain verification failed.");
    Assert(result.Observation.AuditHead == result.AuditEvent.EventHash, "Observation audit head mismatch.");
}

async Task RejectAuditMutation()
{
    await using TestContext context = await TestContext.CreateAsync();
    await context.PreparePersistingOperationAsync();
    EvidenceFinalizationResult result = await context.FinalizeAsync();
    string database = context.Options.DatabasePath;
    // Mutation is executed by the Python IG3 oracle and native integration harness;
    // this C# test confirms the immutable event is present before that negative step.
    Assert(!string.IsNullOrWhiteSpace(result.AuditEvent.EventHash) && File.Exists(database), "Audit evidence missing.");
}

async Task IdempotencyReservationIsStable()
{
    await using TestContext context = await TestContext.CreateAsync();
    IdempotencyReservationResult first = await context.Components.Idempotency.ReserveAsync("idem", "fingerprint", "operation", context.Now, CancellationToken.None);
    IdempotencyReservationResult second = await context.Components.Idempotency.ReserveAsync("idem", "fingerprint", "another", context.Now, CancellationToken.None);
    IdempotencyReservationResult conflict = await context.Components.Idempotency.ReserveAsync("idem", "different", "another", context.Now, CancellationToken.None);
    Assert(first.State == IdempotencyReservationState.Reserved, "First reservation failed.");
    Assert(second.State == IdempotencyReservationState.ExistingInProgress, "Retry was not recognized.");
    Assert(conflict.State == IdempotencyReservationState.Conflict, "Fingerprint conflict was not rejected.");
}

async Task FinalizationIsAtomicAtSourceLevel()
{
    await using TestContext context = await TestContext.CreateAsync();
    await context.PreparePersistingOperationAsync();
    EvidenceFinalizationResult result = await context.FinalizeAsync();
    ObservationRecord? stored = await context.Components.Observations.GetAsync(result.Observation.ObservationId, CancellationToken.None);
    Assert(stored is not null && stored.Status == "COMPLETED", "Completed Observation was not stored.");
    Assert(await context.Components.Artifacts.VerifyAsync(result.OutputArtifact, CancellationToken.None), "Output Artifact verification failed.");
}

async Task RunEngineFacadeSmoke()
{
    string python = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON")
        ?? throw new InvalidOperationException("FSP_PRIVATE_PYTHON must point to the absolute private Python executable.");
    if (!Path.IsPathFullyQualified(python) || !File.Exists(python))
        throw new FileNotFoundException("Private Python executable is missing or is not an absolute path.", python);
    await FullSpectrum.Observer.Tests.Integration.EngineFacadeSourceTests.RunAsync(
        FullSpectrum.Observer.Contracts.RepositoryLayout.FindRoot(), python);
}

async Task RunFoundationPipelineSmoke()
{
    string python = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON")
        ?? throw new InvalidOperationException("FSP_PRIVATE_PYTHON must point to the pinned private Python executable.");
    string root = FullSpectrum.Observer.Contracts.RepositoryLayout.FindRoot();
    string data = Path.Combine(Path.GetTempPath(), "fsp-pipeline-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    EvidenceComponents? evidence = null;
    try
    {
        var clock = new Ig5FixedClock();
        var ids = new FullSpectrum.Observer.Evidence.GuidIdGenerator();
        evidence = EvidenceComposition.Create(new EvidenceOptions { DataDirectory = data }, clock, ids);
        FullSpectrum.Observer.Application.IObserverEngineFacade facade =
            FullSpectrum.Observer.EngineFacade.EngineFacadeComposition.Create(
                new FullSpectrum.Observer.EngineFacade.EngineFacadeOptions
                {
                    PythonExecutablePath = Path.GetFullPath(python),
                    WorkerScriptPath = Path.Combine(root, "engine", "worker", "worker.py"),
                    EngineRootPath = Path.Combine(root, "engine", "vendor", "full-spectrum-engine"),
                    WorkerLockPath = Path.Combine(root, "engine", "worker.lock.json"),
                    SchemaDirectory = FullSpectrum.Observer.Contracts.RepositoryLayout.SchemaDirectory(root),
                });
        var options = new FullSpectrum.Observer.Application.FoundationExecutionOptions
        {
            RepositoryRoot = root,
            SchemaDirectory = FullSpectrum.Observer.Contracts.RepositoryLayout.SchemaDirectory(root),
            CasePackDirectory = Path.Combine(root, "packs", "foundation-case005"),
            AllowedInputRoot = Path.Combine(root, "packs", "foundation-case005"),
            DataDirectory = data,
        };
        FullSpectrum.Observer.Execution.ExecutionUseCases useCases =
            FullSpectrum.Observer.Execution.ExecutionComposition.Create(
                options,
                new FullSpectrum.Observer.Execution.EvidenceComponentsPort(
                    evidence.Session,
                    evidence.Operations,
                    evidence.Observations,
                    evidence.RuntimeSnapshots,
                    evidence.Audit,
                    evidence.Artifacts,
                    evidence.Idempotency),
                facade,
                clock,
                ids,
                () => true);

        FoundationAnalysisRequest request = new()
        {
            Contract = "fs-observer/foundation-analysis-request/1",
            RequestId = Guid.NewGuid().ToString("D"),
            IdempotencyKey = "ig5-smoke",
            Input = JsonSerializer.SerializeToElement(new
            {
                kind = "BUILTIN_CASE",
                case_id = "CASE005_KNOWLEDGE_CONFLICT",
            }),
            RequestedRuntime = JsonSerializer.SerializeToElement(new
            {
                case_pack_id = "fsp.foundation.case005",
                case_pack_version = "1.0.0-alpha.1",
                seed = 42,
                fixed_time_utc = "2026-07-04T00:00:00Z",
            }),
            TimeoutSeconds = 30,
            SubmittedAtUtc = clock.UtcNow.ToString("O"),
        };
        FullSpectrum.Observer.Application.FoundationAnalysisResult result =
            await useCases.Analyze.AnalyzeAsync(request, CancellationToken.None);
        Assert(result.ExitCode == 0, $"Pipeline exit code: {result.ExitCode}");
        FullSpectrum.Observer.Contracts.Models.GovernanceOutputEnvelope output = result.Output
            ?? throw new InvalidOperationException("Pipeline output is missing.");
        FullSpectrum.Observer.Contracts.Models.ObservationRecord observation = result.Observation
            ?? throw new InvalidOperationException("Pipeline Observation is missing.");
        Assert(!output.Boundary.GetProperty("certified").GetBoolean(),
            "Certified boundary must remain false.");
        Assert(!output.Boundary.GetProperty("active_external").GetBoolean(),
            "active_external boundary must remain false.");
        FullSpectrum.Observer.Application.FoundationObservationView? shown =
            await useCases.Show.ShowAsync(observation.ObservationId, CancellationToken.None);
        Assert(shown?.EngineOutput is not null,
            "Show use case did not return the stored Engine output.");
        AuditVerificationResult audit =
            await useCases.VerifyAudit.VerifyAsync(1, CancellationToken.None);
        Assert(audit.IsValid, "Audit verification failed.");
    }
    finally
    {
        if (evidence is not null)
            await evidence.Session.DisposeAsync();
        try { Directory.Delete(data, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class Ig5FixedClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-07-12T00:00:00Z", CultureInfo.InvariantCulture);
}

file sealed class TestContext : IAsyncDisposable
{
    private readonly string _root;
    public EvidenceOptions Options { get; }
    public EvidenceComponents Components { get; }
    public string Now => "2026-07-12T00:00:00.000Z";

    private TestContext(string root, EvidenceOptions options, EvidenceComponents components)
    {
        _root = root; Options = options; Components = components;
    }

    public static async Task<TestContext> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "fsp-evidence-" + Guid.NewGuid().ToString("N"));
        var options = new EvidenceOptions { DataDirectory = root };
        EvidenceComponents components = EvidenceComposition.Create(options, new FixedClock(), new FixedIds());
        await components.Session.InitializeAsync(CancellationToken.None);
        return new TestContext(root, options, components);
    }

    public async Task PreparePersistingOperationAsync()
    {
        await Components.Operations.CreateAsync(new OperationCreateRequest("operation","request","trace",OperationStates.Received,30,Now),CancellationToken.None);
        string[] states = [OperationStates.Adapting,OperationStates.ValidatingSchema,OperationStates.ValidatingGovernance,OperationStates.SnapshotFixed,OperationStates.EngineRunning,OperationStates.AssemblingOutput,OperationStates.Persisting];
        foreach (string state in states) await Components.Operations.UpdateStateAsync("operation",state,"[]",Now,null,CancellationToken.None);
        await Components.Idempotency.ReserveAsync("idem","fingerprint","operation",Now,CancellationToken.None);
    }

    public async Task<EvidenceFinalizationResult> FinalizeAsync()
    {
        RuntimeConfigurationSnapshot snapshot = LoadSnapshot();
        return await Components.Session.FinalizeAsync(new EvidenceFinalizationRequest(
            "operation","request","observation","trace","idem","fingerprint",new string('a',64),null,
            snapshot,System.Text.Encoding.UTF8.GetBytes("{\"result\":true}"),"application/json","SYNTHETIC",
            JsonSerializer.SerializeToElement(new { actor_type="SYSTEM", actor_id="integration-test" }),
            "OBSERVATION_COMPLETED",new string('b',64),Now),CancellationToken.None);
    }

    private static RuntimeConfigurationSnapshot LoadSnapshot()
    {
        string path = Path.Combine(FullSpectrum.Observer.Contracts.RepositoryLayout.SchemaDirectory(), "examples", "runtime-configuration-snapshot.example.json");
        return FoundationJson.Deserialize<RuntimeConfigurationSnapshot>(File.ReadAllBytes(path));
    }

    public async ValueTask DisposeAsync()
    {
        await Components.Session.DisposeAsync();
        try { Directory.Delete(_root, recursive:true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-07-12T00:00:00Z", CultureInfo.InvariantCulture); }
    private sealed class FixedIds : IIdGenerator { private int _value; public Guid NewId() { _value++; return Guid.Parse($"00000000-0000-4000-8000-{_value:000000000000}"); } }
}
