# Build scripts

These scripts require the exact SDK pinned in `global.json`: `10.0.301`.

They never install or download the SDK, packages, Python runtime, or Engine.
`NuGet.Config` intentionally has no online package source in IG1.

## IG2-IG4 runtime variables

- `FSP_PRIVATE_PYTHON`: absolute path to the pinned private Python executable.
- `FSP_SQLITE_NATIVE_DIR`: directory containing the pinned win-x64 `sqlite3.dll`.

The test scripts reject implicit PATH-based runtimes for IG2-IG4.
