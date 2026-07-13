using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Host.Cli;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        return Help();
    if (args[0].Equals("version", StringComparison.OrdinalIgnoreCase))
        return Version(args);

    try
    {
        string command = args[0].ToLowerInvariant();
        CliOptions options = CliOptions.Parse(args.Skip(1));
        string dataDir = Path.GetFullPath(
            options.Get("--data-dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "data"));
        string inputRoot = Path.GetFullPath(
            options.Get("--input-root") ?? Directory.GetCurrentDirectory());
        await using HostComponents host = ObserverHostFactory.Create(dataDir, inputRoot);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        return command switch
        {
            "health" => await HealthAsync(host, options, cts.Token),
            "analyze" => await AnalyzeAsync(host, options, cts.Token),
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
        "  observer verify-audit --from 1 --data-dir PATH --json");
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
        maturity = "IG7_PACKAGE_CANDIDATE_IG8_PENDING",
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
    CancellationToken cancellationToken)
{
    bool hasCase = options.Get("--case") is not null;
    bool hasInput = options.Get("--input") is not null;
    if (hasCase == hasInput)
        throw new ArgumentException("Specify exactly one of --case or --input.");

    string requestId = host.Ids.NewId().ToString("D");
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
    Write(result, options.Has("--json"));
    return result.ExitCode;
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
