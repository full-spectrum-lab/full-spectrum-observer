using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteRuntimeSnapshotRepository : IRuntimeSnapshotRepository
{
    private readonly EvidenceOptions _options;

    public SqliteRuntimeSnapshotRepository(EvidenceOptions options) => _options = options;

    public Task<RuntimeConfigurationSnapshot?> GetAsync(string snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        using SqliteStatement query = connection.Prepare(
            "SELECT snapshot_json FROM runtime_snapshots WHERE snapshot_id=?1;");
        query.BindText(1, snapshotId);
        if (query.Step() != SqliteStepResult.Row)
            return Task.FromResult<RuntimeConfigurationSnapshot?>(null);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(query.GetText(0));
        return Task.FromResult<RuntimeConfigurationSnapshot?>(FoundationJson.Deserialize<RuntimeConfigurationSnapshot>(bytes));
    }
}
