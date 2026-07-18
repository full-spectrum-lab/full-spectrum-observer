using System;
using System.Threading;
using SQLitePCL;

namespace FullSpectrum.Observer.Store;

/// <summary>
/// One-time initialization of the SQLitePCLRaw native runtime.
///
/// <para>M2-RUN-01 adopts the M2-SEC-01 "Candidate A" remediation: the default (vulnerable)
/// SQLitePCLRaw bundle is replaced by the patched <c>SourceGear.sqlite3</c> native library,
/// selected through the <c>e_sqlite3</c> provider. <see cref="Microsoft.Data.Sqlite.Core"/>
/// ships no default provider, so this initialization MUST run before the first
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> is opened — otherwise the connection
/// fails because no native provider is installed.</para>
///
/// <para>The method is idempotent and thread-safe; it may be (and is) called from every Host
/// entry point as well as from the static constructor of <see cref="ObserverStore"/>.</para>
/// </summary>
public static class SqliteRuntimeBootstrap
{
    private static int _initialized;

    /// <summary>Installs the patched SourceGear <c>e_sqlite3</c> provider exactly once.</summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        raw.SetProvider(new SQLite3Provider_e_sqlite3());
    }
}
