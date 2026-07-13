using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    private readonly EvidenceOptions _options;

    public SqliteIdempotencyStore(EvidenceOptions options) => _options = options;

    public Task<IdempotencyReservationResult> ReserveAsync(
        string idempotencyKey,
        string requestFingerprint,
        string operationId,
        string createdAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        connection.BeginImmediate();
        try
        {
            using SqliteStatement query = connection.Prepare(
                "SELECT request_fingerprint,operation_id,observation_id,state FROM idempotency_records WHERE idempotency_key=?1;");
            query.BindText(1, idempotencyKey);
            if (query.Step() == SqliteStepResult.Row)
            {
                string existingFingerprint = query.GetText(0);
                string existingOperation = query.GetText(1);
                string? observation = query.GetNullableText(2);
                string state = query.GetText(3);
                connection.Commit();
                if (!string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
                {
                    return Task.FromResult(new IdempotencyReservationResult(
                        IdempotencyReservationState.Conflict, existingOperation, observation, FoundationReasonCodes.REQ_IDEMPOTENCY_CONFLICT));
                }
                return Task.FromResult(new IdempotencyReservationResult(
                    state == "COMPLETED" ? IdempotencyReservationState.ExistingCompleted : IdempotencyReservationState.ExistingInProgress,
                    existingOperation, observation, null));
            }

            using SqliteStatement insert = connection.Prepare(
                "INSERT INTO idempotency_records(idempotency_key,request_fingerprint,operation_id,observation_id,state,created_at_utc,completed_at_utc) " +
                "VALUES(?1,?2,?3,NULL,'RESERVED',?4,NULL);");
            insert.BindText(1, idempotencyKey);
            insert.BindText(2, requestFingerprint);
            insert.BindText(3, operationId);
            insert.BindText(4, createdAtUtc);
            insert.ExecuteDone();
            connection.Commit();
            return Task.FromResult(new IdempotencyReservationResult(
                IdempotencyReservationState.Reserved, operationId, null, null));
        }
        catch
        {
            connection.RollbackNoThrow();
            throw;
        }
    }
}
