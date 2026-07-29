// <copyright file="Program.cs" company="full-spectrum-observer M2-SEC-01">
// SQLite Native Runtime Security Verification Harness.
// Empirically proves which SQLite native runtime is actually loaded by
// Microsoft.Data.Sqlite and that it satisfies CVE-2025-6965 (>= 3.50.2).
// See README.md for candidate selection and EVIDENCE.md for recorded runs.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

#if USE_CUSTOM_PROVIDER
using SQLitePCL;
#endif

#if CAND_A
const string CandidateName = "A (RECOMMENDED): Microsoft.Data.Sqlite.Core 8.0.10 + SQLitePCLRaw.core 2.1.6 + SQLitePCLRaw.provider.e_sqlite3 2.1.6 + SourceGear.sqlite3";
#elif CAND_A1
const string CandidateName = "A1: Microsoft.Data.Sqlite 8.0.10 (full bundle) + SourceGear.sqlite3";
#elif CAND_B
const string CandidateName = "B: Microsoft.Data.Sqlite 8.0.10 + SQLitePCLRaw.lib.e_sqlite3 2.1.12";
#else
const string CandidateName = "UNKNOWN (set -p:Candidate=A|A1|B)";
#endif

var results = new List<(string Name, bool Pass, string Detail)>();

void Record(string name, bool pass, string detail)
{
    results.Add((name, pass, detail));
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} :: {detail}");
}

Console.WriteLine("============================================================");
Console.WriteLine(" M2-SEC-01  SQLite Native Runtime Security Verification");
Console.WriteLine("============================================================");
Console.WriteLine($" Candidate        : {CandidateName}");
Console.WriteLine($" .NET runtime    : {Environment.Version} | {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($" OS              : {RuntimeInformation.OSDescription}");
Console.WriteLine($" App base dir    : {AppContext.BaseDirectory}");
Console.WriteLine($" DOTNET_ROOT     : {Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "<unset>"}");
Console.WriteLine("------------------------------------------------------------");

// ---------------------------------------------------------------------------
// Provider initialization (candidate-dependent).
// Candidate A swaps the default bundle provider for the e_sqlite3 provider so that
// the patched SourceGear e_sqlite3.dll is loaded INSTEAD of the (absent)
// SQLitePCLRaw.lib.e_sqlite3 bundle.
// ---------------------------------------------------------------------------
#if USE_CUSTOM_PROVIDER
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
Console.WriteLine(" Provider        : SQLitePCLRaw.provider.e_sqlite3 (SetProvider, SourceGear dll)");
#else
Console.WriteLine(" Provider        : default bundle provider (Microsoft.Data.Sqlite auto-init)");
#endif

// Helpers -----------------------------------------------------------------
static (int Major, int Minor, int Patch) ParseVersion(string v)
{
    var parts = v.Split('.');
    int Major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
    int Minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
    int Patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
    return (Major, Minor, Patch);
}

// Lexicographic "at least" comparison for an (major,minor,patch) triple.
// (Avoids relying on the tuple '>=' operator which some SDK/configs reject.)
static bool AtLeast((int Major, int Minor, int Patch) v, int maj, int min, int pat)
{
    if (v.Major != maj)
    {
        return v.Major > maj;
    }

    if (v.Minor != min)
    {
        return v.Minor > min;
    }

    return v.Patch >= pat;
}

static string? ScanEngineVersionFromFile(string path)
{
    try
    {
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var m = Regex.Match(text, @"3\.\d+\.\d+");
        return m.Success ? m.Value : null;
    }
    catch (Exception ex)
    {
        return $"<error: {ex.Message}>";
    }
}

static string NewTempDb()
{
    var p = Path.Combine(Path.GetTempPath(), $"fso_sec01_{Guid.NewGuid():N}.db");
    if (File.Exists(p))
    {
        File.Delete(p);
    }

    return p;
}

// ---------------------------------------------------------------------------
// CHECK 1: actual loaded sqlite engine version (via SELECT sqlite_version())
// ---------------------------------------------------------------------------
string? loadedVersion = null;
{
    var db = NewTempDb();
    try
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        loadedVersion = (string?)cmd.ExecuteScalar();
        conn.Close();
    }
    finally
    {
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }

    var ok = loadedVersion is not null;
    var (maj, min, pat) = ok ? ParseVersion(loadedVersion!) : (0, 0, 0);
    var meetsMin = AtLeast((maj, min, pat), 3, 50, 2);
    Record("1. Loaded sqlite engine >= 3.50.2", ok && meetsMin,
        $"sqlite_version() = '{loadedVersion}' (parsed {(maj, min, pat)}; required >= (3,50,2))");
}

// ---------------------------------------------------------------------------
// CHECK 2 & 3: native module enumeration, old lib not loaded, no conflict
// ---------------------------------------------------------------------------
{
    // Match ONLY the native SQLite engine library (e.g. e_sqlite3.dll / sqlite3.dll),
    // deliberately excluding the managed SQLitePCLRaw.provider.e_sqlite3.dll assembly
    // which only shares the "e_sqlite3" substring.
    var modules = Process.GetCurrentProcess().Modules
        .Cast<ProcessModule>()
        .Where(m => m.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)
                 || m.ModuleName.Equals("sqlite3.dll", StringComparison.OrdinalIgnoreCase))
        .ToList();

    Console.WriteLine("  Loaded native sqlite modules:");
    foreach (var m in modules)
    {
        Console.WriteLine($"    - {m.ModuleName}  =>  {m.FileName}");
    }

    // The vulnerable package cache lives under .packages/sqlitepclraw.lib.e_sqlite3/...
    bool noneFromVulnerableCache = modules.All(m =>
        m.FileName.IndexOf("sqlitepclraw.lib.e_sqlite3", StringComparison.OrdinalIgnoreCase) < 0);

    bool exactlyOne = modules.Count == 1;

    var (lmaj, lmin, lpat) = loadedVersion is not null ? ParseVersion(loadedVersion) : (0, 0, 0);
    bool loadedIsPatched = AtLeast((lmaj, lmin, lpat), 3, 50, 2);

    // Independent cross-check: scan the loaded native dll file for the engine version string.
    string? scanned = null;
    if (modules.Count >= 1)
    {
        scanned = ScanEngineVersionFromFile(modules[0].FileName);
    }

    var (smaj, smin, spat) = scanned is not null ? ParseVersion(scanned) : (0, 0, 0);
    bool fileScanPatched = AtLeast((smaj, smin, spat), 3, 50, 2);

    Record("2. Old e_sqlite3 (<=2.1.11) not loaded",
        noneFromVulnerableCache && loadedIsPatched && fileScanPatched,
        $"loaded module(s)={modules.Count}; from vulnerable cache={!noneFromVulnerableCache}; " +
        $"loaded engine='{loadedVersion}'; file-scan engine='{scanned}'");

    Record("3. No native DLL conflict / duplicate symbol",
        exactlyOne && loadedIsPatched,
        $"distinct e_sqlite3/sqlite3 modules loaded = {modules.Count} (expect 1 = patched SourceGear lib)");
}

// ---------------------------------------------------------------------------
// CHECK 4: CRUD (create / read / update / delete)
// ---------------------------------------------------------------------------
{
    var db = NewTempDb();
    try
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=false");
        conn.Open();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "CREATE TABLE item(id INTEGER PRIMARY KEY, name TEXT NOT NULL, val REAL);";
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "INSERT INTO item(name, val) VALUES('alpha', 1.5),('beta', 2.5);";
            c.ExecuteNonQuery();
        }

        long inserted;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM item;";
            inserted = (long)c.ExecuteScalar()!;
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "UPDATE item SET val = val + 10 WHERE name='alpha';";
            c.ExecuteNonQuery();
        }

        double val;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT val FROM item WHERE name='alpha';";
            val = (double)c.ExecuteScalar()!;
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "DELETE FROM item WHERE name='beta';";
            c.ExecuteNonQuery();
        }

        long remaining;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM item;";
            remaining = (long)c.ExecuteScalar()!;
        }

        conn.Close();

        bool ok = inserted == 2 && Math.Abs(val - 11.5) < 1e-9 && remaining == 1;
        Record("4. CRUD (create/read/update/delete)", ok,
            $"inserted={inserted}, alpha.val after +10 = {val}, remaining after delete = {remaining}");
    }
    finally
    {
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }
}

// ---------------------------------------------------------------------------
// CHECK 5: transactions (COMMIT persists, ROLLBACK reverts)
// ---------------------------------------------------------------------------
{
    var db = NewTempDb();
    try
    {
        // COMMIT path.
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
                c.ExecuteNonQuery();
            }

            using (var tx = conn.BeginTransaction())
            {
                using var c = conn.CreateCommand();
                c.CommandText = "INSERT INTO t(v) VALUES('committed');";
                c.ExecuteNonQuery();
                tx.Commit();
            }

            conn.Close();
        }

        long afterCommit;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM t;";
            afterCommit = (long)c.ExecuteScalar()!;
            conn.Close();
        }

        // ROLLBACK path.
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                using var c = conn.CreateCommand();
                c.CommandText = "INSERT INTO t(v) VALUES('rolledback');";
                c.ExecuteNonQuery();
                tx.Rollback();
            }

            conn.Close();
        }

        long afterRollback;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM t;";
            afterRollback = (long)c.ExecuteScalar()!;
            conn.Close();
        }

        bool ok = afterCommit == 1 && afterRollback == 1;
        Record("5. Transactions (COMMIT persists, ROLLBACK reverts)", ok,
            $"rows after COMMIT={afterCommit} (expect 1), after ROLLBACK={afterRollback} (expect still 1)");
    }
    finally
    {
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }
}

// ---------------------------------------------------------------------------
// CHECK 6: schema migration (ALTER ADD COLUMN + new table, legacy data preserved)
// ---------------------------------------------------------------------------
{
    var db = NewTempDb();
    try
    {
        // v1 schema written by an "older" build.
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "CREATE TABLE evt(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO evt(name) VALUES('e1'),('e2');";
            c.ExecuteNonQuery();
            conn.Close();
        }

        // "Newer" build reopens the existing store and migrates the schema.
        long legacyDefaulted;
        long totalAfterInsert;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "ALTER TABLE evt ADD COLUMN processed INTEGER NOT NULL DEFAULT 0;";
                c.ExecuteNonQuery();
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT); INSERT INTO meta(k,v) VALUES('schema_version','2');";
                c.ExecuteNonQuery();
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM evt WHERE processed = 0;";
                legacyDefaulted = (long)c.ExecuteScalar()!;
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "INSERT INTO evt(name, processed) VALUES('e3', 1);";
                c.ExecuteNonQuery();
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM evt;";
                totalAfterInsert = (long)c.ExecuteScalar()!;
            }

            conn.Close();
        }

        // Reopen again to confirm the migration persisted.
        long totalAfterReopen;
        int e3Processed;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM evt;";
                totalAfterReopen = (long)c.ExecuteScalar()!;
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT processed FROM evt WHERE name='e3';";
                e3Processed = Convert.ToInt32(c.ExecuteScalar()!);
            }

            conn.Close();
        }

        bool ok = legacyDefaulted == 2 && totalAfterReopen == 3 && e3Processed == 1;
        Record("6. Schema migration (ALTER ADD COLUMN + new table, old data preserved)", ok,
            $"legacy rows defaulted processed=0: {legacyDefaulted}/2; rows after reopen = {totalAfterReopen} (expect 3); e3.processed = {e3Processed} (expect 1)");
    }
    finally
    {
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }
}

// ---------------------------------------------------------------------------
// CHECK 7: restart recovery (persistence + in-flight marker recoverable)
// ---------------------------------------------------------------------------
{
    var db = NewTempDb();
    try
    {
        // "Process 1": create the store, write committed rows incl. an in-flight marker, exit.
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "CREATE TABLE job(id INTEGER PRIMARY KEY, payload TEXT, status TEXT NOT NULL DEFAULT 'pending');";
            c.ExecuteNonQuery();
            using var i = conn.CreateCommand();
            i.CommandText = "INSERT INTO job(payload, status) VALUES('j1','done'),('j2','inflight');";
            i.ExecuteNonQuery();
            conn.Close(); // simulate process exit
        }

        // "Process 2" (restart): reopen the SAME store.
        long totalRows;
        long inflightAtRestart;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM job;";
                totalRows = (long)c.ExecuteScalar()!;
            }

            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM job WHERE status='inflight';";
                inflightAtRestart = (long)c.ExecuteScalar()!;
            }

            // Recover in-flight work.
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "UPDATE job SET status='done' WHERE status='inflight';";
                c.ExecuteNonQuery();
            }

            conn.Close();
        }

        // Verify the recovery persisted across the reopen.
        long remainingInflight;
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=false"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM job WHERE status='inflight';";
            remainingInflight = (long)c.ExecuteScalar()!;
            conn.Close();
        }

        bool ok = totalRows == 2 && inflightAtRestart == 1 && remainingInflight == 0;
        Record("7. Restart recovery (persistence + in-flight marker recoverable)", ok,
            $"rows after restart = {totalRows} (expect 2); in-flight at restart = {inflightAtRestart} (expect 1); in-flight after recovery = {remainingInflight} (expect 0)");
    }
    finally
    {
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }
}

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------
Console.WriteLine("------------------------------------------------------------");
int pass = results.Count(r => r.Pass);
int fail = results.Count - pass;
foreach (var r in results)
{
    Console.WriteLine($"  {(r.Pass ? "[PASS]" : "[FAIL]")} {r.Name}");
}
Console.WriteLine("------------------------------------------------------------");
Console.WriteLine($" RESULT: {pass}/{results.Count} checks passed, {fail} failed.");
Console.WriteLine(" NOTE: NuGetAudit (check #8) is validated by the build step, not in-process.");
Console.WriteLine("       Run:  dotnet build -c Release  (default audit ON) and confirm NO NU1903.");
Console.WriteLine("============================================================");

return fail == 0 ? 0 : 1;
