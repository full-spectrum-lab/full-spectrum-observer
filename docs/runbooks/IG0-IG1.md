# IG0 / IG1 runbook

1. Install the exact .NET SDK from `global.json`.
2. Open PowerShell 7 at the repository root.
3. Run `pwsh ./scripts/verify-baseline.ps1`.
4. Confirm `evidence/ig0/baseline-verify.json` reports PASS.
5. Run `pwsh ./scripts/build.ps1 -Configuration Release -Locked`.
6. Run `pwsh ./scripts/test.ps1 -Gate IG1`.
7. Archive `evidence/ig1/build-log.txt`.
8. Do not start IG2 if restore/build fails or any baseline hash differs.
