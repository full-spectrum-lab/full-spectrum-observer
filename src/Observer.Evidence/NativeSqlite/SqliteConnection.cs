using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Evidence.NativeSqlite;

internal sealed class SqliteConnection : IDisposable
{
    private IntPtr _handle;

    private SqliteConnection(IntPtr handle) => _handle = handle;

    internal IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new ObjectDisposedException(nameof(SqliteConnection));

    public static SqliteConnection Open(string path, int busyTimeoutMilliseconds)
    {
        int rc = NativeSqlite.sqlite3_open_v2(
            path,
            out IntPtr handle,
            NativeSqlite.OpenReadWrite | NativeSqlite.OpenCreate | NativeSqlite.OpenFullMutex,
            IntPtr.Zero);
        if (rc != NativeSqlite.Ok)
        {
            EvidenceStoreException error = NativeSqlite.Error(handle, "open", rc);
            if (handle != IntPtr.Zero)
            {
                _ = NativeSqlite.sqlite3_close_v2(handle);
            }
            throw error;
        }

        var connection = new SqliteConnection(handle);
        connection.Ensure(NativeSqlite.sqlite3_extended_result_codes(handle, 1), "extended_result_codes");
        connection.Ensure(NativeSqlite.sqlite3_busy_timeout(handle, busyTimeoutMilliseconds), "busy_timeout");
        return connection;
    }

    public void Execute(string sql)
    {
        int rc = NativeSqlite.sqlite3_exec(Handle, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr errorPointer);
        if (rc == NativeSqlite.Ok)
        {
            return;
        }

        string detail = NativeSqlite.Utf8(errorPointer);
        if (errorPointer != IntPtr.Zero)
        {
            NativeSqlite.sqlite3_free(errorPointer);
        }
        throw new EvidenceStoreException(
            rc is 5 or 6 ? FoundationReasonCodes.STORE_LOCKED : FoundationReasonCodes.STORE_WRITE_FAILED,
            $"SQLite exec failed (rc={rc}): {detail}");
    }

    public SqliteStatement Prepare(string sql)
    {
        int rc = NativeSqlite.sqlite3_prepare_v2(Handle, sql, -1, out IntPtr statement, IntPtr.Zero);
        Ensure(rc, "prepare");
        return new SqliteStatement(this, statement);
    }

    public int Changes => NativeSqlite.sqlite3_changes(Handle);

    public void BeginImmediate() => Execute("BEGIN IMMEDIATE;");
    public void Commit() => Execute("COMMIT;");

    public void RollbackNoThrow()
    {
        try { Execute("ROLLBACK;"); }
        catch (EvidenceStoreException) { }
    }

    internal void Ensure(int rc, string operation)
    {
        if (rc != NativeSqlite.Ok)
        {
            throw NativeSqlite.Error(Handle, operation, rc);
        }
    }

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = NativeSqlite.sqlite3_close_v2(handle);
        }
    }
}
