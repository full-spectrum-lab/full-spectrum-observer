using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteOperationStore : IOperationStore
{
    private readonly EvidenceOptions _options;

    public SqliteOperationStore(EvidenceOptions options) => _options = options;

    public Task CreateAsync(OperationCreateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        using SqliteStatement statement = connection.Prepare(
            "INSERT INTO operations(operation_id,request_id,trace_id,state,reason_json,started_at_utc,updated_at_utc,completed_at_utc,timeout_seconds) " +
            "VALUES(?1,?2,?3,?4,'[]',?5,?5,NULL,?6);");
        statement.BindText(1, request.OperationId);
        statement.BindText(2, request.RequestId);
        statement.BindText(3, request.TraceId);
        statement.BindText(4, request.State);
        statement.BindText(5, request.UpdatedAtUtc);
        statement.BindInt64(6, request.TimeoutSeconds);
        statement.ExecuteDone();
        return Task.CompletedTask;
    }

    public async Task UpdateStateAsync(string operationId, string state, string reasonJson, string updatedAtUtc, string? completedAtUtc, CancellationToken cancellationToken)
    {
        OperationRecord current = await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvidenceStoreException(FoundationReasonCodes.STORE_WRITE_FAILED, "Operation record does not exist.");
        OperationStateMachine.EnsureTransition(current.State, state);

        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        using SqliteStatement statement = connection.Prepare(
            "UPDATE operations SET state=?1,reason_json=?2,updated_at_utc=?3,completed_at_utc=?4 WHERE operation_id=?5;");
        statement.BindText(1, state);
        statement.BindText(2, reasonJson);
        statement.BindText(3, updatedAtUtc);
        statement.BindNullableText(4, completedAtUtc);
        statement.BindText(5, operationId);
        statement.ExecuteDone();
        if (connection.Changes != 1)
        {
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_WRITE_FAILED, "Operation state update affected an unexpected number of rows.");
        }
    }

    public Task<OperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        using SqliteStatement statement = connection.Prepare(
            "SELECT operation_id,request_id,trace_id,state,reason_json,started_at_utc,updated_at_utc,completed_at_utc,timeout_seconds " +
            "FROM operations WHERE operation_id=?1;");
        statement.BindText(1, operationId);
        if (statement.Step() != SqliteStepResult.Row) return Task.FromResult<OperationRecord?>(null);
        return Task.FromResult<OperationRecord?>(new OperationRecord(
            statement.GetText(0), statement.GetText(1), statement.GetText(2), statement.GetText(3), statement.GetText(4),
            statement.GetNullableText(5), statement.GetText(6), statement.GetNullableText(7), checked((int)statement.GetInt64(8))));
    }
}
