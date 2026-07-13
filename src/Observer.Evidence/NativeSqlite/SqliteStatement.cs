namespace FullSpectrum.Observer.Evidence.NativeSqlite;

internal enum SqliteStepResult
{
    Row,
    Done,
}

internal sealed class SqliteStatement : IDisposable
{
    private readonly SqliteConnection _connection;
    private IntPtr _handle;

    internal SqliteStatement(SqliteConnection connection, IntPtr handle)
    {
        _connection = connection;
        _handle = handle;
    }

    private IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new ObjectDisposedException(nameof(SqliteStatement));

    public void BindText(int index, string value) =>
        _connection.Ensure(NativeSqlite.sqlite3_bind_text(Handle, index, value, -1, NativeSqlite.Transient), "bind_text");

    public void BindInt64(int index, long value) =>
        _connection.Ensure(NativeSqlite.sqlite3_bind_int64(Handle, index, value), "bind_int64");

    public void BindNull(int index) =>
        _connection.Ensure(NativeSqlite.sqlite3_bind_null(Handle, index), "bind_null");

    public void BindNullableText(int index, string? value)
    {
        if (value is null) BindNull(index); else BindText(index, value);
    }

    public SqliteStepResult Step()
    {
        int rc = NativeSqlite.sqlite3_step(Handle);
        return rc switch
        {
            NativeSqlite.Row => SqliteStepResult.Row,
            NativeSqlite.Done => SqliteStepResult.Done,
            _ => throw NativeSqlite.Error(_connection.Handle, "step", rc),
        };
    }

    public void ExecuteDone()
    {
        if (Step() != SqliteStepResult.Done)
        {
            throw new InvalidDataException("SQLite statement unexpectedly returned a row.");
        }
    }

    public bool IsNull(int column) => NativeSqlite.sqlite3_column_type(Handle, column) == 5;

    public string GetText(int column)
    {
        IntPtr pointer = NativeSqlite.sqlite3_column_text(Handle, column);
        int bytes = NativeSqlite.sqlite3_column_bytes(Handle, column);
        return NativeSqlite.Utf8(pointer, bytes);
    }

    public string? GetNullableText(int column) => IsNull(column) ? null : GetText(column);
    public long GetInt64(int column) => NativeSqlite.sqlite3_column_int64(Handle, column);

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = NativeSqlite.sqlite3_finalize(handle);
        }
    }
}
