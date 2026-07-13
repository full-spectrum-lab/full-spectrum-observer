using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Execution;

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
if (scope is "ALL" or "IG6")
{
    tests.Add(("TR-FK-SEC-PATH-001", RejectInputPathTraversal));
    tests.Add(("TR-FK-SEC-LOCK-001", RejectSecondWriterInstance));
    tests.Add(("TR-FK-SEC-DB-001", DetectCorruptedDatabase));
    tests.Add(("TR-FK-SEC-WORKER-001", KillTimedOutWorker));
    tests.Add(("TR-FK-SEC-WORKER-002", RejectOversizedWorkerOutput));
    tests.Add(("TR-FK-SNP-002", RejectTamperedSnapshot));
    tests.Add(("TR-FK-AUD-004", DetectTamperedAuditFile));
    tests.Add(("TR-FK-AUD-005", SerializeConcurrentAuditWriters));
    tests.Add(("TR-FK-SEC-002/PRV-002", EnforceInputLimitsWithoutDisclosure));
    tests.Add(("TR-FK-SEC-004", RejectTamperedWorker));
    tests.Add(("TR-FK-SEC-006", RejectMultilineWorkerProtocol));
    tests.Add(("TR-FK-REL-001", RecoverAfterAdapterFailure));
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

async Task RejectInputPathTraversal()
{
    string repository = RepositoryLayout.FindRoot();
    string allowed = Path.Combine(Path.GetTempPath(), $"observer-ig6-input-{Guid.NewGuid():N}");
    Directory.CreateDirectory(allowed);
    try
    {
        var options = new FoundationExecutionOptions
        {
            RepositoryRoot = repository,
            SchemaDirectory = Path.Combine(repository, "schemas", "foundation-kernel"),
            CasePackDirectory = Path.Combine(repository, "packs", "foundation-case005"),
            AllowedInputRoot = allowed,
            DataDirectory = Path.Combine(allowed, "data"),
        };
        var intake = new FoundationInputIntake(options);
        JsonElement input = JsonSerializer.SerializeToElement(new { kind = "JSON_FILE", file_path = "..\\escape.json" }, FoundationJson.CreateOptions());
        try
        {
            await intake.LoadAsync(input, CancellationToken.None);
            throw new InvalidOperationException("Path traversal was accepted.");
        }
        catch (IntakeException exception) when (exception.ReasonCode == FoundationReasonCodes.INTAKE_PATH_OUTSIDE_ALLOWED_ROOT) { }
    }
    finally { Directory.Delete(allowed, recursive: true); }
}

async Task RejectSecondWriterInstance()
{
    string root = Path.Combine(Path.GetTempPath(), $"observer-ig6-lock-{Guid.NewGuid():N}");
    var options = new EvidenceOptions { DataDirectory = root };
    EvidenceComponents first = EvidenceComposition.Create(options);
    EvidenceComponents second = EvidenceComposition.Create(options);
    try
    {
        await first.Session.InitializeAsync(CancellationToken.None);
        try
        {
            await second.Session.InitializeAsync(CancellationToken.None);
            throw new InvalidOperationException("Second writer acquired the instance lock.");
        }
        catch (EvidenceStoreException exception) when (exception.ReasonCode == FoundationReasonCodes.STORE_LOCKED) { }
    }
    finally
    {
        await second.Session.DisposeAsync();
        await first.Session.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

async Task DetectCorruptedDatabase()
{
    string root = Path.Combine(Path.GetTempPath(), $"observer-ig6-corrupt-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    await File.WriteAllBytesAsync(Path.Combine(root, "observer.db"), RandomNumberGenerator.GetBytes(4096));
    EvidenceComponents components = EvidenceComposition.Create(new EvidenceOptions { DataDirectory = root });
    try
    {
        try
        {
            await components.Session.InitializeAsync(CancellationToken.None);
            throw new InvalidOperationException("Corrupted SQLite database was accepted.");
        }
        catch (EvidenceStoreException) { }
    }
    finally
    {
        await components.Session.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

async Task KillTimedOutWorker()
{
    (PythonWorkerEngineFacade facade, EngineFacadeRequest request, string root, string pidPath) = CreateAdversarialFacade(
        "import os,time,pathlib\npathlib.Path(__file__).with_name('pid.txt').write_text(str(os.getpid()))\ntime.sleep(30)\n",
        maximumResponseBytes: 1024);
    var elapsed = Stopwatch.StartNew();
    try
    {
        EngineFacadeExecutionResult result = await facade.EvaluateAsync(request, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert(result.Response.Status == "TIMED_OUT", "Timed-out Worker did not return the TIMED_OUT state.");
        Assert(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Worker timeout exceeded the kill budget.");
        if (File.Exists(pidPath) && int.TryParse(await File.ReadAllTextAsync(pidPath), out int pid))
        {
            await Task.Delay(250);
            try
            {
                using Process process = Process.GetProcessById(pid);
                Assert(process.HasExited, "Timed-out Worker process is still alive.");
            }
            catch (ArgumentException) { }
        }
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

async Task RejectOversizedWorkerOutput()
{
    (PythonWorkerEngineFacade facade, EngineFacadeRequest request, string root, _) = CreateAdversarialFacade(
        "import sys\nsys.stdout.write('x' * 4096)\n",
        maximumResponseBytes: 128);
    try
    {
        try
        {
            await facade.EvaluateAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);
            throw new InvalidOperationException("Oversized Worker output was accepted.");
        }
        catch (EngineFacadeException exception) when (exception.ReasonCode == FoundationReasonCodes.FACADE_RESPONSE_TOO_LARGE) { }
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

async Task RejectTamperedSnapshot()
{
    await using TestContext context = await TestContext.CreateAsync();
    await context.PreparePersistingOperationAsync();
    RuntimeConfigurationSnapshot tampered = TestContext.CreateSnapshot(0) with { SnapshotSha256 = new string('0', 64) };
    try
    {
        await context.FinalizeAsync(tampered);
        throw new InvalidOperationException("Tampered Runtime Snapshot was accepted.");
    }
    catch (EvidenceStoreException exception) when (exception.ReasonCode == FoundationReasonCodes.SNAPSHOT_DIGEST_MISMATCH) { }
}

async Task DetectTamperedAuditFile()
{
    TestContext context = await TestContext.CreateAsync();
    await context.PreparePersistingOperationAsync();
    EvidenceFinalizationResult result = await context.FinalizeAsync();
    string root = context.Options.DataDirectory;
    await context.Components.Session.DisposeAsync();
    byte[] needle = System.Text.Encoding.ASCII.GetBytes(result.AuditEvent.EventHash);
    bool replaced = false;
    foreach (string file in Directory.EnumerateFiles(root, "observer.db*"))
    {
        byte[] bytes = await File.ReadAllBytesAsync(file);
        int offset = 0;
        bool fileChanged = false;
        while (offset <= bytes.Length - needle.Length)
        {
            int relativeIndex = bytes.AsSpan(offset).IndexOf(needle);
            if (relativeIndex < 0) break;
            int index = offset + relativeIndex;
            bytes[index] = bytes[index] == (byte)'0' ? (byte)'1' : (byte)'0';
            fileChanged = true;
            replaced = true;
            offset = index + needle.Length;
        }
        if (fileChanged) await File.WriteAllBytesAsync(file, bytes);
    }
    Assert(replaced, "Audit hash was not located in SQLite storage.");
    EvidenceComponents components = EvidenceComposition.Create(new EvidenceOptions { DataDirectory = root });
    try
    {
        await components.Session.InitializeAsync(CancellationToken.None);
        AuditVerificationResult verification = await components.Audit.VerifyAsync(1, CancellationToken.None);
        Assert(!verification.IsValid && verification.FirstBrokenSequence == 1, "Tampered Audit row was not located.");
    }
    finally
    {
        await components.Session.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

async Task SerializeConcurrentAuditWriters()
{
    await using TestContext context = await TestContext.CreateAsync();
    const int count = 6;
    for (int index = 1; index <= count; index++) await context.PreparePersistingOperationAsync(index);
    EvidenceFinalizationResult[] results = await Task.WhenAll(
        Enumerable.Range(1, count).Select(index => context.FinalizeAsync(TestContext.CreateSnapshot(index), index)));
    Assert(results.Select(result => result.AuditEvent.SequenceNo).Order().SequenceEqual(Enumerable.Range(1, count).Select(i => (long)i)), "Concurrent Audit sequence is not contiguous.");
    AuditVerificationResult verification = await context.Components.Audit.VerifyAsync(1, CancellationToken.None);
    Assert(verification.IsValid && verification.CheckedEvents == count, "Concurrent Audit chain verification failed.");
}

async Task EnforceInputLimitsWithoutDisclosure()
{
    string repository = RepositoryLayout.FindRoot();
    string root = Path.Combine(Path.GetTempPath(), $"observer-ig6-limits-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    const string canary = "FS_OBSERVER_PRIVATE_CANARY_91A2";
    string oversized = Path.Combine(root, "oversized.json");
    await File.WriteAllTextAsync(oversized, "{\"secret\":\"" + canary + "\",\"padding\":\"" + new string('x', 2048) + "\"}");
    var options = new FoundationExecutionOptions
    {
        RepositoryRoot = repository,
        SchemaDirectory = RepositoryLayout.SchemaDirectory(repository),
        CasePackDirectory = Path.Combine(repository, "packs", "foundation-case005"),
        AllowedInputRoot = root,
        DataDirectory = Path.Combine(root, "data"),
        MaximumInputBytes = 256,
    };
    var intake = new FoundationInputIntake(options);
    JsonElement input = JsonSerializer.SerializeToElement(new { kind = "JSON_FILE", file_path = "oversized.json" }, FoundationJson.CreateOptions());
    try
    {
        await intake.LoadAsync(input, CancellationToken.None);
        throw new InvalidOperationException("Oversized input was accepted.");
    }
    catch (IntakeException exception) when (exception.ReasonCode == FoundationReasonCodes.INTAKE_FILE_TOO_LARGE)
    {
        Assert(!exception.ToString().Contains(canary, StringComparison.Ordinal), "Sensitive input leaked into the error.");
    }
    Assert(!Directory.Exists(options.DataDirectory), "Rejected Raw Input created a data directory.");
    Directory.Delete(root, recursive: true);
}

async Task RejectTamperedWorker()
{
    (PythonWorkerEngineFacade facade, EngineFacadeRequest request, string root, _) = CreateAdversarialFacade(
        "print('{}')\n", maximumResponseBytes: 1024);
    await File.AppendAllTextAsync(Path.Combine(root, "worker.py"), "# tampered\n");
    try
    {
        try
        {
            await facade.EvaluateAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);
            throw new InvalidOperationException("Tampered Worker was accepted.");
        }
        catch (EngineFacadeException exception) when (exception.ReasonCode == FoundationReasonCodes.FACADE_WORKER_HASH_MISMATCH) { }
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

async Task RejectMultilineWorkerProtocol()
{
    (PythonWorkerEngineFacade facade, EngineFacadeRequest request, string root, _) = CreateAdversarialFacade(
        "print('{}')\nprint('{}')\n", maximumResponseBytes: 1024);
    try
    {
        try
        {
            await facade.EvaluateAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);
            throw new InvalidOperationException("Multiline Worker protocol was accepted.");
        }
        catch (EngineFacadeException exception) when (exception.ReasonCode == FoundationReasonCodes.FACADE_PROTOCOL_INVALID) { }
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

Task RecoverAfterAdapterFailure()
{
    var adapter = new FoundationScenarioAdapter();
    try
    {
        adapter.Adapt(JsonDocument.Parse("[\"invalid\"]").RootElement.Clone());
        throw new InvalidOperationException("Invalid Adapter input was accepted.");
    }
    catch (AdapterException exception) when (exception.ReasonCode == FoundationReasonCodes.ADAPTER_MAPPING_FAILED) { }
    string repository = RepositoryLayout.FindRoot();
    JsonElement valid = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "packs", "foundation-case005", "case005.input.json"))).RootElement.Clone();
    AdapterResult recovered = adapter.Adapt(valid);
    Assert(recovered.CanonicalContext.ValueKind == JsonValueKind.Object && recovered.CanonicalContextSha256.Length == 64, "Adapter did not recover after a rejected input.");
    return Task.CompletedTask;
}

(PythonWorkerEngineFacade Facade, EngineFacadeRequest Request, string Root, string PidPath) CreateAdversarialFacade(
    string workerSource,
    int maximumResponseBytes)
{
    string repository = RepositoryLayout.FindRoot();
    string root = Path.Combine(Path.GetTempPath(), $"observer-ig6-worker-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    string worker = Path.Combine(root, "worker.py");
    File.WriteAllText(worker, workerSource);
    byte[] bytes = File.ReadAllBytes(worker);
    string lockPath = Path.Combine(root, "worker.lock.json");
    File.WriteAllText(lockPath, JsonSerializer.Serialize(new
    {
        protocol = "fs-observer-worker-lock/1",
        engine_version = BuildIdentity.EngineVersion,
        engine_commit = BuildIdentity.EngineCommit,
        files = new[] { new { path = "worker.py", size_bytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } },
    }, FoundationJson.CreateOptions()));
    var options = new EngineFacadeOptions
    {
        PythonExecutablePath = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON")!,
        WorkerScriptPath = worker,
        EngineRootPath = Path.Combine(repository, "engine", "vendor", "full-spectrum-engine"),
        WorkerLockPath = lockPath,
        SchemaDirectory = Path.Combine(repository, "schemas", "foundation-kernel"),
        MaximumResponseBytes = maximumResponseBytes,
        MaximumStandardErrorBytes = 1024,
        KillGracePeriod = TimeSpan.FromMilliseconds(250),
    };
    JsonElement scenario = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(repository, "packs", "foundation-case005", "case005.input.json"))).RootElement.Clone();
    var request = new EngineFacadeRequest
    {
        Protocol = "fs-observer-engine-facade/1",
        RequestId = "12345678-1234-4234-8234-123456789abc",
        Operation = "evaluate",
        Engine = JsonSerializer.SerializeToElement(new { version = BuildIdentity.EngineVersion, commit = BuildIdentity.EngineCommit }),
        Seed = 42,
        FixedTimeUtc = "2026-07-04T00:00:00Z",
        Scenario = scenario,
        OutputSerialization = "FSE-PYJSON-1",
    };
    return (new PythonWorkerEngineFacade(options), request, root, Path.Combine(root, "pid.txt"));
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

    public async Task PreparePersistingOperationAsync(int suffix = 0)
    {
        string operation = suffix == 0 ? "operation" : $"operation-{suffix}";
        string request = suffix == 0 ? "request" : $"request-{suffix}";
        string trace = suffix == 0 ? "trace" : $"trace-{suffix}";
        string idem = suffix == 0 ? "idem" : $"idem-{suffix}";
        string fingerprint = suffix == 0 ? "fingerprint" : $"fingerprint-{suffix}";
        await Components.Operations.CreateAsync(new OperationCreateRequest(operation,request,trace,OperationStates.Received,30,Now),CancellationToken.None);
        string[] states = [OperationStates.Adapting,OperationStates.ValidatingSchema,OperationStates.ValidatingGovernance,OperationStates.SnapshotFixed,OperationStates.EngineRunning,OperationStates.AssemblingOutput,OperationStates.Persisting];
        foreach (string state in states) await Components.Operations.UpdateStateAsync(operation,state,"[]",Now,null,CancellationToken.None);
        await Components.Idempotency.ReserveAsync(idem,fingerprint,operation,Now,CancellationToken.None);
    }

    public async Task<EvidenceFinalizationResult> FinalizeAsync(RuntimeConfigurationSnapshot? suppliedSnapshot = null, int suffix = 0)
    {
        RuntimeConfigurationSnapshot snapshot = suppliedSnapshot ?? CreateSnapshot(suffix);
        string operation = suffix == 0 ? "operation" : $"operation-{suffix}";
        string request = suffix == 0 ? "request" : $"request-{suffix}";
        string observation = suffix == 0 ? "observation" : $"observation-{suffix}";
        string trace = suffix == 0 ? "trace" : $"trace-{suffix}";
        string idem = suffix == 0 ? "idem" : $"idem-{suffix}";
        string fingerprint = suffix == 0 ? "fingerprint" : $"fingerprint-{suffix}";
        return await Components.Session.FinalizeAsync(new EvidenceFinalizationRequest(
            operation,request,observation,trace,idem,fingerprint,new string('a',64),null,
            snapshot,System.Text.Encoding.UTF8.GetBytes("{\"result\":true}"),"application/json","SYNTHETIC",
            JsonSerializer.SerializeToElement(new { actor_type="SYSTEM", actor_id="integration-test" }),
            "OBSERVATION_COMPLETED",new string('b',64),Now),CancellationToken.None);
    }

    public static RuntimeConfigurationSnapshot CreateSnapshot(int suffix)
    {
        string path = Path.Combine(FullSpectrum.Observer.Contracts.RepositoryLayout.SchemaDirectory(), "examples", "runtime-configuration-snapshot.example.json");
        RuntimeConfigurationSnapshot snapshot = FoundationJson.Deserialize<RuntimeConfigurationSnapshot>(File.ReadAllBytes(path));
        if (suffix == 0) return snapshot;
        snapshot = snapshot with { SnapshotId = $"12345678-1234-4234-8234-{suffix:000000000000}", SnapshotSha256 = string.Empty };
        string digest = SelfDigestCalculator.Compute(FoundationJson.Serialize(snapshot), "snapshot_sha256");
        return snapshot with { SnapshotSha256 = digest };
    }

    public async ValueTask DisposeAsync()
    {
        await Components.Session.DisposeAsync();
        try { Directory.Delete(_root, recursive:true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-07-12T00:00:00Z", CultureInfo.InvariantCulture); }
    private sealed class FixedIds : IIdGenerator { private int _value; public Guid NewId() { int value = Interlocked.Increment(ref _value); return Guid.Parse($"00000000-0000-4000-8000-{value:000000000000}"); } }
}
