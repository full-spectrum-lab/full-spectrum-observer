# SqliteRuntimeVerify — M2-SEC-01 verification harness

Standalone, solution-excluded console tool that **empirically** proves which SQLite native runtime
`Microsoft.Data.Sqlite` actually loads, for NU1903 / CVE-2025-6965 remediation.

## Run
```bash
# set the isolated SDK
export DOTNET_ROOT="/path/to/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
FEED="https://api.nuget.org/v3/index.json"

dotnet restore SqliteRuntimeVerify.csproj -s "$FEED"
dotnet build  SqliteRuntimeVerify.csproj -c Release --no-restore
dotnet bin/Release/net10.0/win-x64/SqliteRuntimeVerify.dll
```
Checks 1–7 run in-process; check #8 (NuGetAudit default ON) is proven by the `restore` step
emitting **zero NU1903** and the transitive graph containing no vulnerable `SQLitePCLRaw.lib.e_sqlite3`.

## Candidates (edit `<Candidate>` in the csproj; see EVIDENCE.md caveat on switching)
- `A`  (default, RECOMMENDED): `Microsoft.Data.Sqlite.Core` 8.0.10 + `SQLitePCLRaw.core` 2.1.6 +
       `SQLitePCLRaw.provider.e_sqlite3` 2.1.6 + `SourceGear.sqlite3` → patched engine ≥ 3.50.2.
- `A1`: full `Microsoft.Data.Sqlite` 8.0.10 bundle + `SourceGear.sqlite3` → vulnerable lib stays in
       graph (negative control; NuGetAudit still fails).
- `B`:  `Microsoft.Data.Sqlite` 8.0.10 + `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 → empirically patched
       engine, but advisory "Patched=None" → not adopted.

See `EVIDENCE.md` for full results and the determined adoption (Candidate A).
