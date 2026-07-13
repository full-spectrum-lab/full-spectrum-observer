# Native SQLite runtime

The C# Evidence implementation uses the SQLite C API through `DllImport("sqlite3")`
so the source build has no NuGet provider dependency.

The Windows portable package must include a pinned `sqlite3.dll` under the final
runtime search path. Its exact version and SHA-256 are a WP-07 build artifact and
must be recorded in `sqlite-runtime.lock.json` before IG7.

IG3 cannot be marked PASS until the Windows native runtime is supplied and the C#
integration tests execute against it.
