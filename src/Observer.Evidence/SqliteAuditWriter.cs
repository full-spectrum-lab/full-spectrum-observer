using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteAuditWriter : IAuditWriter
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly SemaphoreSlim SingleWriter = new(1, 1);
    private readonly EvidenceOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public SqliteAuditWriter(EvidenceOptions options, IClock clock, IIdGenerator ids)
    {
        _options = options;
        _clock = clock;
        _ids = ids;
    }

    internal async Task<IDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
    {
        await SingleWriter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(SingleWriter);
    }

    internal AuditEventContract AppendWithinTransaction(
        SqliteConnection connection,
        string eventType,
        JsonElement actor,
        string observationId,
        string operationId,
        string traceId,
        string payloadDigest)
    {
        long sequence = 1;
        string previousHash = GenesisHash;
        using (SqliteStatement head = connection.Prepare(
            "SELECT sequence_no,event_hash FROM audit_events ORDER BY sequence_no DESC LIMIT 1;"))
        {
            if (head.Step() == SqliteStepResult.Row)
            {
                sequence = checked(head.GetInt64(0) + 1);
                previousHash = head.GetText(1);
            }
        }

        var auditEvent = new AuditEventContract
        {
            Contract = "fs-observer/audit-event/1",
            EventId = _ids.NewId().ToString("D"),
            StreamId = "GLOBAL",
            SequenceNo = sequence,
            EventType = eventType,
            OccurredAtUtc = _clock.UtcNow.ToString("O"),
            Actor = actor.Clone(),
            ObservationId = observationId,
            OperationId = operationId,
            TraceId = traceId,
            PayloadDigest = payloadDigest,
            PayloadMediaType = "application/json",
            SerializationId = FsObsCanonicalizer.SerializationId,
            PreviousHash = previousHash,
            EventHash = string.Empty,
        };
        string hash = SelfDigestCalculator.Compute(FoundationJson.Serialize(auditEvent), "event_hash");
        auditEvent = auditEvent with { EventHash = hash };

        using SqliteStatement insert = connection.Prepare(
            "INSERT INTO audit_events(sequence_no,event_id,stream_id,event_type,occurred_at_utc,actor_json,observation_id,operation_id,trace_id,payload_digest,payload_media_type,serialization_id,previous_hash,event_hash) " +
            "VALUES(?1,?2,'GLOBAL',?3,?4,?5,?6,?7,?8,?9,'application/json','FS-OBS-CANON-1',?10,?11);");
        insert.BindInt64(1, sequence);
        insert.BindText(2, auditEvent.EventId);
        insert.BindText(3, eventType);
        insert.BindText(4, auditEvent.OccurredAtUtc);
        insert.BindText(5, auditEvent.Actor.GetRawText());
        insert.BindText(6, observationId);
        insert.BindText(7, operationId);
        insert.BindText(8, traceId);
        insert.BindText(9, payloadDigest);
        insert.BindText(10, previousHash);
        insert.BindText(11, hash);
        insert.ExecuteDone();
        return auditEvent;
    }

    public Task<AuditVerificationResult> VerifyAsync(long fromSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long start = Math.Max(1, fromSequence);
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        string expectedPrevious = GenesisHash;
        if (start > 1)
        {
            using SqliteStatement previous = connection.Prepare("SELECT event_hash FROM audit_events WHERE sequence_no=?1;");
            previous.BindInt64(1, start - 1);
            if (previous.Step() != SqliteStepResult.Row)
            {
                return Task.FromResult(new AuditVerificationResult(false, 0, start, FoundationReasonCodes.AUDIT_CHAIN_BROKEN, GenesisHash));
            }
            expectedPrevious = previous.GetText(0);
        }

        using SqliteStatement query = connection.Prepare(
            "SELECT sequence_no,event_id,event_type,occurred_at_utc,actor_json,observation_id,operation_id,trace_id,payload_digest,payload_media_type,serialization_id,previous_hash,event_hash " +
            "FROM audit_events WHERE sequence_no>=?1 ORDER BY sequence_no;");
        query.BindInt64(1, start);
        long checkedEvents = 0;
        long expectedSequence = start;
        string headHash = expectedPrevious;
        while (query.Step() == SqliteStepResult.Row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sequence = query.GetInt64(0);
            string previousHash = query.GetText(11);
            string storedHash = query.GetText(12);
            using JsonDocument actorDocument = JsonDocument.Parse(query.GetText(4));
            var auditEvent = new AuditEventContract
            {
                Contract = "fs-observer/audit-event/1",
                EventId = query.GetText(1),
                StreamId = "GLOBAL",
                SequenceNo = sequence,
                EventType = query.GetText(2),
                OccurredAtUtc = query.GetText(3),
                Actor = actorDocument.RootElement.Clone(),
                ObservationId = query.GetNullableText(5),
                OperationId = query.GetNullableText(6),
                TraceId = query.GetText(7),
                PayloadDigest = query.GetText(8),
                PayloadMediaType = query.GetText(9),
                SerializationId = query.GetText(10),
                PreviousHash = previousHash,
                EventHash = storedHash,
            };
            string calculated = SelfDigestCalculator.Compute(FoundationJson.Serialize(auditEvent), "event_hash");
            if (sequence != expectedSequence || !string.Equals(previousHash, expectedPrevious, StringComparison.Ordinal) || !string.Equals(calculated, storedHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new AuditVerificationResult(false, checkedEvents, sequence, FoundationReasonCodes.AUDIT_CHAIN_BROKEN, headHash));
            }
            checkedEvents++;
            expectedSequence++;
            expectedPrevious = storedHash;
            headHash = storedHash;
        }
        return Task.FromResult(new AuditVerificationResult(true, checkedEvents, null, null, headHash));
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
