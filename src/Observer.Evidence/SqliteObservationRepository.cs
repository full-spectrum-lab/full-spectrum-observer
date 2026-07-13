using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteObservationRepository : IObservationRepository
{
    private readonly EvidenceOptions _options;

    public SqliteObservationRepository(EvidenceOptions options) => _options = options;

    public Task<ObservationRecord?> GetAsync(string observationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        using SqliteStatement statement = connection.Prepare(
            "SELECT o.observation_id,o.operation_id,o.request_id,o.trace_id,o.status,o.input_sha256,o.canonical_context_sha256," +
            "o.runtime_snapshot_id,o.audit_head,o.idempotency_key,o.created_at_utc,o.updated_at_utc," +
            "a.artifact_id,a.media_type,a.sha256,a.size_bytes,a.relative_path,a.classification,a.created_at_utc " +
            "FROM observations o LEFT JOIN artifacts a ON a.artifact_id=o.output_artifact_id WHERE o.observation_id=?1;");
        statement.BindText(1, observationId);
        if (statement.Step() != SqliteStepResult.Row) return Task.FromResult<ObservationRecord?>(null);

        JsonElement? outputRef = null;
        if (!statement.IsNull(12))
        {
            outputRef = JsonSerializer.SerializeToElement(new
            {
                artifact_id = statement.GetText(12),
                media_type = statement.GetText(13),
                sha256 = statement.GetText(14),
                size_bytes = statement.GetInt64(15),
                relative_path = statement.GetText(16),
                classification = statement.GetText(17),
                created_at_utc = statement.GetText(18),
            });
        }

        return Task.FromResult<ObservationRecord?>(new ObservationRecord
        {
            Contract = "fs-observer/observation-record/1",
            ObservationId = statement.GetText(0),
            OperationId = statement.GetText(1),
            RequestId = statement.GetText(2),
            TraceId = statement.GetText(3),
            Status = statement.GetText(4),
            InputSha256 = statement.GetText(5),
            CanonicalContextSha256 = statement.GetNullableText(6),
            RuntimeSnapshotId = statement.GetText(7),
            OutputRef = outputRef,
            AuditHead = statement.GetText(8),
            IdempotencyKey = statement.GetNullableText(9),
            CreatedAtUtc = statement.GetText(10),
            UpdatedAtUtc = statement.GetText(11),
        });
    }
}
