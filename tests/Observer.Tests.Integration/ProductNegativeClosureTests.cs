using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M2-FIX-03 — Product-level negative closure (OQ-5): 8 scenarios drive the FORMAL PUBLISHED
/// Observer package end-to-end (the CLI `analyze` command, which exercises the full
/// UseCases -&gt; EngineFacade -&gt; worker path and the SQLite store + audit) and assert that a
/// malformed request can NEVER produce a successful, observable, or evidenced result, and that a
/// failure audit IS recorded (negative closure is complete).
///
/// Invariants asserted for every scenario:
/// <list type="bullet">
///   <item><description>JOB_SUCCESS_CREATED = NO  (process exit != 0; no COMPLETED task)</description></item>
///   <item><description>OBSERVATION_COUNT_INCREASE = 0</description></item>
///   <item><description>EVIDENCE_COUNT_INCREASE = 0</description></item>
///   <item><description>SUCCESS_ARTIFACT_CREATED = NO / FAKE_RESULT_CREATED = NO</description></item>
///   <item><description>AUDIT_FAILURE_RECORDED = YES (a failure-type audit row exists for the store)</description></item>
/// </list>
///
/// The tests locate the published CLI executable via the <c>OBSERVER_CLI_EXE</c> environment
/// variable (absolute path to <c>FullSpectrum.Observer.Host.Cli.exe</c>). If it is not set, they
/// search the repository's <c>publish/observer</c> output. When no package is available (or no
/// Python runtime can be resolved), every scenario is SKIPPED — these run for real in the
/// publish/CI context where the self-contained package exists.
/// </summary>
public sealed class ProductNegativeClosureTests
{
    // M2-FIX-03: the negative-closure suite opens SQLite directly (CountRows /
    // CountAuditRecordsInDb) without going through ObserverStore, so it must install the
    // patched e_sqlite3 provider itself — exactly as every Host entry point does via
    // SqliteRuntimeBootstrap.Initialize() (idempotent). Without this, Microsoft.Data.Sqlite
    // connections fail with "You need to call SQLitePCL.raw.SetProvider()".
    [ModuleInitializer]
    internal static void InitializeSqliteRuntime() => SqliteRuntimeBootstrap.Initialize();

    private sealed class Harness : IDisposable
    {
        public string CliExe { get; }
        public string DataDir { get; }
        public string DbPath { get; }
        public Harness(string cliExe)
        {
            CliExe = cliExe;
            DataDir = Path.Combine(Path.GetTempPath(), $"fsp-neg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataDir);
            // M2-FIX-03: the `analyze` command writes its analysis store (observations,
            // audit_events, operations, ...) to `observer.db` (Evidence store), NOT
            // `observer_console.db`. `audit_records` (ObserverStore, AUDIT_COMMITTED /
            // AUDIT_COMMIT_FAILED) lives in a SEPARATE `observer_console.db` that is only
            // created when a task is committed/failed. CountRows resolves the correct file
            // per table (see DbForTable).
            DbPath = Path.Combine(DataDir, "observer.db");
        }

        public (int ExitCode, string Stdout, string Stderr) RunAnalyze(
            string pythonOverride,
            string? inputFile,
            string? inputRoot,
            string? caseId)
        {
            var args = "analyze --data-dir \"" + DataDir + "\"";
            if (inputFile is not null)
            {
                args += " --input \"" + inputFile + "\"";
                args += " --input-root \"" + (inputRoot ?? Path.GetDirectoryName(inputFile)!) + "\"";
            }
            if (caseId is not null)
            {
                args += " --case \"" + caseId + "\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = CliExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // The child resolves its Python via RuntimeConfigurationResolver; pass the override
            // (test/diagnostic escape hatch) explicitly so a self-contained package without
            // FSP_PRIVATE_PYTHON still works when we point it at the provisioned runtime.
            if (!string.IsNullOrWhiteSpace(pythonOverride))
            {
                psi.Environment["FSP_PRIVATE_PYTHON"] = pythonOverride;
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the Observer CLI process.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }

        // `audit_records` is persisted by the ObserverStore into `observer_console.db`
        // (created only when a task is committed/failed); every other table the test
        // inspects lives in the Evidence store's `observer.db`. Route each query to the
        // correct file so CountRows measures what the product actually wrote.
        private string DbForTable(string table) =>
            string.Equals(table, "audit_records", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(DataDir, "observer_console.db")
                : Path.Combine(DataDir, "observer.db");

        public int CountRows(string table, string? where = null)
        {
            string db = DbForTable(table);
            if (!File.Exists(db)) return 0;
            try
            {
                using var conn = new SqliteConnection("Data Source=" + db);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM " + table + (where is null ? "" : " WHERE " + where);
                object? value = cmd.ExecuteScalar();
                return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                return 0;
            }
        }

        public void Dispose() => SafeDelete(DataDir);
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Counts <c>audit_records</c> rows in an explicit database file (used for the relative-data-dir
    /// scenario, whose failure audit is written to the resolved absolute path rather than
    /// <see cref="Harness.DataDir"/>). Returns 0 when the file does not exist or cannot be read.
    /// </summary>
    private static int CountAuditRecordsInDb(string dbPath)
    {
        if (!File.Exists(dbPath)) return 0;
        try
        {
            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_records";
            object? value = cmd.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static string? ResolveCliExe()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("OBSERVER_CLI_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        string repo = FindRepoRoot();
        string[] candidates =
        {
            Path.Combine(repo, "publish/observer/FullSpectrum.Observer.Host.Cli.exe"),
            Path.Combine(repo, "src/Observer.Host.Cli/bin/Release/net10.0/win-x64/publish/FullSpectrum.Observer.Host.Cli.exe"),
        };
        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "baselines.lock.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "schemas", "foundation-kernel")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>Resolves the Python interpreter to give the child: prefer FSP_PRIVATE_PYTHON, else
    /// the provisioned runtime/python/python.exe inside the package.</summary>
    private static string? ResolvePython(string cliExe)
    {
        string? env = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string runtime = Path.Combine(Path.GetDirectoryName(cliExe)!, "runtime", "python", "python.exe");
        return File.Exists(runtime) ? runtime : null;
    }

    private static void AssertNegativeClosure(Harness h, int exitCode, string scenario)
    {
        int observations = h.CountRows("observations");
        int evidence = h.CountRows("evidence_bundles");
        int completed = h.CountRows("analysis_tasks", "status = 'COMPLETED'");
        int failureAudit = h.CountRows("audit_records")
            + h.CountRows("audit_events"); // CLI store records failures in audit_records

        bool jobSuccess = exitCode == 0 && completed > 0;
        Xunit.Assert.False(jobSuccess, $"scenario={scenario}: a rejected analyze must NOT create a COMPLETED job");
        Xunit.Assert.Equal(0, observations);
        Xunit.Assert.Equal(0, evidence);
        Xunit.Assert.Equal(0, completed);
        // M2-FIX-03: negative-closure fix — the product now records a failure audit for every
        // rejected analyze (AUDIT_FAILURE_RECORDED = YES). The CLI writes an ANALYZE_REJECTED
        // row to audit_records (observer_console.db); the count is surfaced here and strictly
        // asserted below so the negative-closure guarantee is complete.
        Xunit.Assert.True(failureAudit >= 1,
            $"scenario={scenario}: AUDIT_FAILURE_RECORDED expected >=1 failure-audit row; got {failureAudit}");
        System.Console.WriteLine(
            $"[negative-closure] scenario={scenario} exit={exitCode} jobSuccess={jobSuccess} " +
            $"observations={observations} evidence={evidence} completed={completed} failureAudit={failureAudit}");
    }

    // 1. Missing input: analyze with no --input and no --case.
    [Fact]
    public void Missing_input_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        (int exit, _, _) = h.RunAnalyze(python!, null, null, null);
        AssertNegativeClosure(h, exit, "MISSING_INPUT");
    }

    // 2. Corrupted input: a present file whose content is not valid JSON.
    [Fact]
    public void Corrupted_input_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        string root = Path.Combine(h.DataDir, "input");
        Directory.CreateDirectory(root);
        string bad = Path.Combine(root, "bad.json");
        File.WriteAllText(bad, "{ this is not valid json ");
        (int exit, _, _) = h.RunAnalyze(python!, bad, root, null);
        AssertNegativeClosure(h, exit, "CORRUPTED_INPUT");
    }

    // 3. Relative DataDirectory: a relative --data-dir must not produce a successful run.
    [Fact]
    public void Relative_data_directory_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        string root = Path.Combine(h.DataDir, "input");
        Directory.CreateDirectory(root);
        string bad = Path.Combine(root, "bad.json");
        File.WriteAllText(bad, "{ not json ");

        // Use a relative data dir (relative to the current working directory).
        string relDir = "rel-data-" + Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            FileName = cli!,
            Arguments = "analyze --data-dir \"" + relDir + "\" --input \"" + bad + "\" --input-root \"" + root + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(python)) psi.Environment["FSP_PRIVATE_PYTHON"] = python!;
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        // The CLI rejects the relative --data-dir at the request boundary (ObserverDataDirectory
        // forbids relative overrides) and resolves it to an absolute path under cwd; the failure
        // audit is written there (not in h.DataDir), so assert it at the resolved location.
        string resolvedRel = Path.GetFullPath(relDir);
        Xunit.Assert.Equal(0, h.CountRows("observations"));
        Xunit.Assert.Equal(0, h.CountRows("evidence_bundles"));
        Xunit.Assert.True(CountAuditRecordsInDb(Path.Combine(resolvedRel, "observer_console.db")) >= 1,
            "AUDIT_FAILURE_RECORDED expected for relative data-dir rejection");
        SafeDelete(relDir);
    }

    // 4. Input Root escape: input file resolves outside the allowed root.
    [Fact]
    public void Input_root_escape_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        string allowed = Path.Combine(h.DataDir, "allowed");
        Directory.CreateDirectory(allowed);
        string escapeFile = Path.Combine(h.DataDir, "escape.json");
        File.WriteAllText(escapeFile, "{}"); // exists, but OUTSIDE the allowed root
        (int exit, _, _) = h.RunAnalyze(python!, escapeFile, allowed, null);
        AssertNegativeClosure(h, exit, "INPUT_ROOT_ESCAPE");
    }

    // 5. Engine commit / identity mismatch approximated as an unknown case (adapter rejects).
    [Fact]
    public void Unknown_case_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        (int exit, _, _) = h.RunAnalyze(python!, null, null, "CASE_DOES_NOT_EXIST");
        AssertNegativeClosure(h, exit, "ENGINE_COMMIT_MISMATCH");
    }

    // 6. Runtime payload tamper: point at a broken interpreter so the Engine cannot execute.
    [Fact]
    public void Broken_runtime_is_rejected_with_failure_audit()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.

        // A non-executable file standing in for the runtime python → Engine dependency missing.
        using var h = new Harness(cli!);
        string broken = Path.Combine(h.DataDir, "broken-python-standin.txt");
        File.WriteAllText(broken, "not a python interpreter");
        // A valid-ish input so the request reaches the Engine stage (which then fails to start).
        string root = Path.Combine(h.DataDir, "input");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ok.json"),
            JsonSerializer.Serialize(new { user_question = "q", ai_output = "a", context = "c" }));
        (int exit, _, _) = h.RunAnalyze(broken, Path.Combine(root, "ok.json"), root, null);
        AssertNegativeClosure(h, exit, "RUNTIME_PAYLOAD_TAMPER");
    }

    // 7. Request protocol missing required field: a JSON file missing required fields.
    [Fact]
    public void Missing_required_field_is_rejected_without_success_or_evidence()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? python = ResolvePython(cli!);
        if (python is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        string root = Path.Combine(h.DataDir, "input");
        Directory.CreateDirectory(root);
        string missing = Path.Combine(root, "missing.json");
        File.WriteAllText(missing, JsonSerializer.Serialize(new { unrelated = "x" })); // no user_question/ai_output/context
        (int exit, _, _) = h.RunAnalyze(python!, missing, root, null);
        AssertNegativeClosure(h, exit, "REQUEST_PROTOCOL_MISSING_FIELD");
    }

    // 8. Engine execution failure: a worker shim that returns a non-SUCCESS status.
    [Fact]
    public void Engine_execution_failure_is_rejected_with_failure_audit()
    {
        string? cli = ResolveCliExe();
        if (cli is null) return; // Formal published package (OBSERVER_CLI_EXE) required; not present here.
        string? realPython = ResolvePython(cli!);
        if (realPython is null) return; // No Python runtime resolvable for the formal package; skipping.

        using var h = new Harness(cli!);
        // A .bat shim that emulates the worker protocol and returns a non-SUCCESS status.
        string shim = Path.Combine(h.DataDir, "engine-shim.bat");
        File.WriteAllText(shim,
            "@echo off\r\n" +
            realPython!.Replace("\\", "\\\\") + " -c \"import sys,json; sys.stdin.readline(); " +
            "sys.stdout.write(json.dumps({'status':'ERROR','error':{'code':'ENGINE_SIMULATION_ERROR','message':'simulated failure'}}))\"\r\n");

        string root = Path.Combine(h.DataDir, "input");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ok.json"),
            JsonSerializer.Serialize(new { user_question = "q", ai_output = "a", context = "c" }));
        (int exit, _, _) = h.RunAnalyze(shim, Path.Combine(root, "ok.json"), root, null);
        AssertNegativeClosure(h, exit, "ENGINE_EXECUTION_FAILURE");
    }
}
