using System.Runtime.InteropServices;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Evidence.NativeSqlite;

internal static class NativeSqlite
{
    internal const int Ok = 0;
    internal const int Row = 100;
    internal const int Done = 101;
    internal const int OpenReadWrite = 0x00000002;
    internal const int OpenCreate = 0x00000004;
    internal const int OpenFullMutex = 0x00010000;
    internal static readonly IntPtr Transient = new(-1);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_extended_result_codes(IntPtr db, int onoff);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_extended_errcode(IntPtr db);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr callbackArg, out IntPtr errorMessage);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sqlite3_free(IntPtr pointer);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_step(IntPtr statement);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int byteCount, IntPtr destructor);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_null(IntPtr statement, int index);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_changes(IntPtr db);

    internal static string Utf8(IntPtr pointer, int bytes = -1) => pointer == IntPtr.Zero
        ? string.Empty
        : bytes >= 0
            ? Marshal.PtrToStringUTF8(pointer, bytes) ?? string.Empty
            : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

    internal static EvidenceStoreException Error(IntPtr db, string operation, int resultCode)
    {
        int extended = db == IntPtr.Zero ? resultCode : sqlite3_extended_errcode(db);
        string message = db == IntPtr.Zero ? "SQLite database handle is unavailable." : Utf8(sqlite3_errmsg(db));
        string reason = resultCode is 5 or 6 ? FoundationReasonCodes.STORE_LOCKED : FoundationReasonCodes.STORE_WRITE_FAILED;
        return new EvidenceStoreException(reason, $"SQLite {operation} failed (rc={resultCode}, extended={extended}): {message}");
    }
}
