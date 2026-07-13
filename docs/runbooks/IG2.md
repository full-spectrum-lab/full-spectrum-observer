# IG2 Contracts Executable runbook

Precondition: IG1 real `.NET restore/build` is PASS.

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
dotnet run --project tests/Observer.Tests.Unit -c Release --no-restore
dotnet run --project tests/Observer.Tests.Contract -c Release --no-restore
python ./scripts/verify-architecture.py
python ./scripts/ig2-reference-validator.py
```

IG2 may close only when the C# build and both C# runners pass. The Python reference
validator is an oracle/evidence helper, not the product runtime implementation.
