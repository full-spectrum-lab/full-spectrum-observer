using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Host.Cli;
using FullSpectrum.Observer.Store;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        return Help();
    if (args[0].Equals("version", StringComparison.OrdinalIgnoreCase))
        return Version(args);

    // Declared before the try so the catch blocks (which record the analyze failure audit)
    // can see which command was being dispatched.
    string command = args[0].ToLowerInvariant();
    // Declared before the try so the catch blocks (which record the analyze failure audit) can
    // read the resolved options even when the rejection happens before a successful dispatch.
    CliOptions options = null!;
    try
    {
        // Install the patched SourceGear native SQLite provider before any connection opens.
        SqliteRuntimeBootstrap.Initialize();

        options = CliOptions.Parse(args.Skip(1));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        // `serve` is a launcher-only command: it runs the embedded web host and the
        // console SQLite store, but never needs the source repository root, schema
        // directory, or embedded engine. It MUST launch from any working directory
        // (including a published product directory started from an unrelated cwd),
        // so it is dispatched before ObserverHostFactory.Create, which requires the
        // repository root to be discoverable from the working directory.
        if (command == "serve")
        {
            return await ServeAsync(options, cts.Token);
        }

        string dataDir = ObserverDataDirectory.Resolve(options.Get("--data-dir"));
        string inputRoot = Path.GetFullPath(
            options.Get("--input-root") ?? Directory.GetCurrentDirectory());
        await using HostComponents host = ObserverHostFactory.Create(dataDir, inputRoot);

        return command switch
        {
            "health" => await HealthAsync(host, options, cts.Token),
            "analyze" => await AnalyzeAsync(host, options, dataDir, cts.Token),
            "show" => await ShowAsync(host, options, cts.Token),
            "verify-audit" => await VerifyAuditAsync(host, options, cts.Token),
            _ => Unsupported(command),
        };
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Observer command was cancelled.");
        return 60;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        // M2-FIX-03: negative-closure fix — an `analyze` rejection at the request boundary
        // (relative/invalid --data-dir, or missing --case/--input) must still leave a failure
        // audit so AUDIT_FAILURE_RECORDED = YES holds for the negative-closure suite. The
        // rejection happens before the analyze handler runs, so it is recorded here.
        if (string.Equals(command, "analyze", StringComparison.OrdinalIgnoreCase))
        {
            string rejectedDir = Path.GetFullPath(options?.Get("--data-dir") ?? ".");
            await RecordAnalyzeRejectionAuditAsync(rejectedDir, Guid.NewGuid().ToString("D"));
        }
        return 2;
    }
    catch (FileNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 70;
    }
    catch (DirectoryNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 70;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Observer command failed: {exception.GetType().Name}. Details are redacted.");
        return 70;
    }
}

static int Help()
{
    Console.WriteLine(
        "Full Spectrum Observer Foundation Kernel source candidate\n" +
        "Commands:\n" +
        "  observer version --json\n" +
        "  observer health --data-dir PATH --json\n" +
        "  observer analyze --case CASE005_KNOWLEDGE_CONFLICT --data-dir PATH --json\n" +
        "  observer analyze --input FILE --input-root ROOT --data-dir PATH --json\n" +
        "  observer show --observation-id UUID --data-dir PATH --json\n" +
        "  observer verify-audit --from 1 --data-dir PATH --json\n" +
        "  observer serve    启动 Web 控制台（默认仅监听 127.0.0.1）");
    return 0;
}

static int Version(string[] args)
{
    bool json = args.Any(static value => value.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var value = new
    {
        system_version = BuildIdentity.SystemVersion,
        implementation_gate = BuildIdentity.ImplementationGate,
        scope_baseline = BuildIdentity.ScopeBaseline,
        design_baseline = BuildIdentity.DesignBaseline,
        implementation_baseline = BuildIdentity.ImplementationBaseline,
        schema_baseline = BuildIdentity.SchemaBaseline,
        engine_version = BuildIdentity.EngineVersion,
        engine_commit = BuildIdentity.EngineCommit,
        maturity = BuildIdentity.ImplementationGate,
    };
    Console.WriteLine(json
        ? JsonSerializer.Serialize(value, FoundationJson.CreateOptions())
        : $"Observer {value.system_version} / {value.maturity}");
    return 0;
}

static async Task<int> HealthAsync(
    HostComponents host,
    CliOptions options,
    CancellationToken cancellationToken)
{
    FoundationHealthResult result = await host.UseCases.Health.CheckAsync(cancellationToken);
    Write(result, options.Has("--json"));
    return result.IsHealthy ? 0 : 70;
}

static async Task<int> AnalyzeAsync(
    HostComponents host,
    CliOptions options,
    string dataDir,
    CancellationToken cancellationToken)
{
    // Request-shape validation that rejects before any task is created. This throws to the
    // MainAsync ArgumentException catch, which records the failure audit — it must stay OUTSIDE
    // the try below so it is not double-recorded here.
    bool hasCase = options.Get("--case") is not null;
    bool hasInput = options.Get("--input") is not null;
    if (hasCase == hasInput)
        throw new ArgumentException("Specify exactly one of --case or --input.");

    string requestId = host.Ids.NewId().ToString("D");
    try
    {
        string idempotency = options.Get("--idempotency-key") ?? requestId;
        int timeout = options.GetInt("--timeout", 30);
        object input = hasCase
            ? new { kind = "BUILTIN_CASE", case_id = options.Require("--case") }
            : new { kind = "JSON_FILE", file_path = options.Require("--input") };

        FoundationAnalysisRequest request = new()
        {
            Contract = "fs-observer/foundation-analysis-request/1",
            RequestId = requestId,
            IdempotencyKey = idempotency,
            Input = JsonSerializer.SerializeToElement(input, FoundationJson.CreateOptions()),
            RequestedRuntime = JsonSerializer.SerializeToElement(new
            {
                case_pack_id = "fsp.foundation.case005",
                case_pack_version = "1.0.0-alpha.1",
                seed = 42,
                fixed_time_utc = "2026-07-04T00:00:00Z",
            }, FoundationJson.CreateOptions()),
            TimeoutSeconds = timeout,
            SubmittedAtUtc = host.Clock.UtcNow.ToString("O"),
        };

        FoundationAnalysisResult result = await host.UseCases.Analyze.AnalyzeAsync(request, cancellationToken);
        // M2-FIX-03: negative-closure fix — the product MUST leave a failure audit for every
        // rejected analysis (the use case returns a non-zero exit code instead of throwing).
        // This makes AUDIT_FAILURE_RECORDED = YES hold for all negative-closure scenarios.
        if (result.ExitCode != 0)
        {
            await RecordAnalyzeRejectionAuditAsync(dataDir, requestId);
        }
        Write(result, options.Has("--json"));
        return result.ExitCode;
    }
    catch (Exception)
    {
        // Any unexpected failure while servicing the request also leaves a failure audit.
        await RecordAnalyzeRejectionAuditAsync(dataDir, requestId);
        throw;
    }
}

// M2-FIX-03: negative-closure fix — records a failure audit when an `analyze` request is
// rejected or fails, so the product always leaves an audit trail for a rejected analysis
// (AUDIT_FAILURE_RECORDED = YES). The record is written to the console store
// (observer_console.db / audit_records) chained to the latest existing record.
// Best-effort: a failure here must never mask or escalate the original rejection.
static async Task RecordAnalyzeRejectionAuditAsync(string dataDir, string requestId)
{
    try
    {
        string dbPath = Path.Combine(dataDir, "observer_console.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        AuditRecord? prev = await store.GetLatestAuditAsync();
        string at = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        string canonical = $"{prev?.AuditId ?? string.Empty}|ANALYZE_REJECTED|{at}|{requestId}|{Environment.UserName}|{Environment.MachineName}";
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        // A rejected request has no analysis task yet, so task_id is null (audit_records.task_id
        // is a foreign key to analysis_tasks, which does not exist for a rejected request).
        var record = AuditRecord.Append(
            auditId: "AUD-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6],
            taskId: null,
            action: "ANALYZE_REJECTED",
            windowsUser: Environment.UserName,
            machine: Environment.MachineName,
            session: requestId,
            atUtc: at,
            digest: digest,
            previousAuditId: prev?.AuditId);
        await store.AppendAuditAsync(record);
    }
    catch
    {
        // Best-effort: never mask the original rejection with an audit-write failure.
    }
}

static async Task<int> ShowAsync(
    HostComponents host,
    CliOptions options,
    CancellationToken cancellationToken)
{
    string observationId = options.Require("--observation-id");
    FoundationObservationView? result = await host.UseCases.Show.ShowAsync(observationId, cancellationToken);
    if (result is null)
    {
        Console.Error.WriteLine("Observation not found.");
        return 10;
    }
    Write(result, options.Has("--json"));
    return 0;
}

static async Task<int> VerifyAuditAsync(
    HostComponents host,
    CliOptions options,
    CancellationToken cancellationToken)
{
    int from = options.GetInt("--from", 1);
    if (from < 1)
        throw new ArgumentException("--from must be at least 1.");
    AuditVerificationResult result = await host.UseCases.VerifyAudit.VerifyAsync(from, cancellationToken);
    Write(result, options.Has("--json"));
    return result.IsValid ? 0 : 50;
}

static async Task<int> ServeAsync(CliOptions options, CancellationToken cancellationToken)
{
    SqliteRuntimeBootstrap.Initialize();
    string dataDir = ObserverDataDirectory.Resolve(options.Get("--data-dir"));
    Console.WriteLine($"[Observer] Resolved data directory: {dataDir}");
    string dbPath = Path.Combine(dataDir, "observer_console.db");

    var store = new ObserverStore(dbPath);
    await store.EnsureSchemaAsync();

    var clock = new SystemClock();
    var ids = new GuidIdGenerator();
    using var launcher = new Launcher(store, clock, ids, dataDir);
    return await launcher.RunAsync(cancellationToken);
}

static void Write<T>(T value, bool json)
{
    JsonSerializerOptions serializerOptions = FoundationJson.CreateOptions();
    if (!json)
        serializerOptions.WriteIndented = true;
    Console.WriteLine(JsonSerializer.Serialize(value, serializerOptions));
}

static int Unsupported(string command)
{
    Console.Error.WriteLine($"Unsupported command: {command}");
    return 2;
}
