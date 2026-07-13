using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private readonly EvidenceOptions _options;

    private InstanceLock(FileStream stream, string path, EvidenceOptions options)
    {
        _stream = stream;
        _path = path;
        _options = options;
    }

    public static InstanceLock Acquire(EvidenceOptions options, string startedAtUtc, string packageSha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.InstanceLockPath)!);
        try
        {
            var stream = new FileStream(options.InstanceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                owner_pid = Environment.ProcessId,
                started_at_utc = startedAtUtc,
                package_sha256 = packageSha256,
            });
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return new InstanceLock(stream, options.InstanceLockPath, options);
        }
        catch (IOException exception)
        {
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_LOCKED, "Another Observer writer instance holds the data directory.", exception);
        }
    }

    public void RegisterDatabaseRow(EvidenceOptions options, string startedAtUtc, string packageSha256)
    {
        using SqliteConnection connection = SqliteConnection.Open(options.DatabasePath, options.BusyTimeoutMilliseconds);
        connection.BeginImmediate();
        try
        {
            connection.Execute("DELETE FROM instance_lock WHERE lock_id=1;");
            using SqliteStatement statement = connection.Prepare(
                "INSERT INTO instance_lock(lock_id,owner_pid,started_at_utc,package_sha256) VALUES(1,?1,?2,?3);");
            statement.BindInt64(1, Environment.ProcessId);
            statement.BindText(2, startedAtUtc);
            statement.BindText(3, packageSha256);
            statement.ExecuteDone();
            connection.Commit();
        }
        catch
        {
            connection.RollbackNoThrow();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
            using SqliteStatement statement = connection.Prepare("DELETE FROM instance_lock WHERE lock_id=1 AND owner_pid=?1;");
            statement.BindInt64(1, Environment.ProcessId);
            statement.ExecuteDone();
        }
        catch (EvidenceStoreException) { }
        _stream.Dispose();
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
