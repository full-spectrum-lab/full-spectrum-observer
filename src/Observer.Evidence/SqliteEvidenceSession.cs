using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.Evidence.NativeSqlite;

namespace FullSpectrum.Observer.Evidence;

public sealed class SqliteEvidenceSession : IEvidenceSession
{
    private readonly EvidenceOptions _options;
    private readonly IClock _clock;
    private readonly IArtifactStore _artifacts;
    private readonly SqliteAuditWriter _audit;
    private InstanceLock? _instanceLock;
    private bool _initialized;

    public SqliteEvidenceSession(
        EvidenceOptions options,
        IClock clock,
        IArtifactStore artifacts,
        SqliteAuditWriter audit)
    {
        _options = options;
        _clock = clock;
        _artifacts = artifacts;
        _audit = audit;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized) return Task.CompletedTask;
        Directory.CreateDirectory(Path.GetFullPath(_options.DataDirectory));
        Directory.CreateDirectory(_options.ArtifactRoot);
        Directory.CreateDirectory(_options.TemporaryRoot);
        string now = _clock.UtcNow.ToString("O");
        _instanceLock = InstanceLock.Acquire(_options, now, BuildIdentity.ImplementationBaseline);
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        SqliteSchemaMigrator.Apply(connection, now);
        _instanceLock.RegisterDatabaseRow(_options, now, BuildIdentity.ImplementationBaseline);
        _initialized = true;
        return Task.CompletedTask;
    }

    public async Task<EvidenceFinalizationResult> FinalizeAsync(EvidenceFinalizationRequest request, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        byte[] snapshotJson = FoundationJson.Serialize(request.RuntimeSnapshot);
        string calculatedSnapshot = SelfDigestCalculator.Compute(snapshotJson, "snapshot_sha256");
        if (!string.Equals(calculatedSnapshot, request.RuntimeSnapshot.SnapshotSha256, StringComparison.Ordinal))
        {
            throw new EvidenceStoreException(FoundationReasonCodes.SNAPSHOT_DIGEST_MISMATCH, "Runtime Snapshot digest mismatch.");
        }

        ArtifactDescriptor artifact = await _artifacts.WriteAsync(new ArtifactWriteRequest(
            request.EngineOutputBytes,
            request.EngineOutputMediaType,
            request.Classification), cancellationToken).ConfigureAwait(false);

        using IDisposable writerLease = await _audit.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds);
        connection.BeginImmediate();
        try
        {
            InsertSnapshot(connection, request.RuntimeSnapshot, snapshotJson);
            ArtifactDescriptor persistedArtifact = ResolveOrInsertArtifact(connection, artifact);
            AuditEventContract auditEvent = _audit.AppendWithinTransaction(
                connection,
                request.AuditEventType,
                request.AuditActor,
                request.ObservationId,
                request.OperationId,
                request.TraceId,
                request.AuditPayloadDigest);
            ObservationRecord observation = InsertObservation(connection, request, persistedArtifact, auditEvent.EventHash);
            CompleteIdempotency(connection, request);
            CompleteOperation(connection, request.OperationId, request.CreatedAtUtc);
            connection.Commit();

            if (!await _artifacts.VerifyAsync(persistedArtifact, cancellationToken).ConfigureAwait(false))
            {
                throw new EvidenceStoreException(FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED, "Post-commit Artifact verification failed.");
            }
            return new EvidenceFinalizationResult(persistedArtifact, observation, auditEvent);
        }
        catch
        {
            connection.RollbackNoThrow();
            throw;
        }
    }

    public Task<int> CleanupOrphanArtifactsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds))
        using (SqliteStatement query = connection.Prepare("SELECT relative_path FROM artifacts;"))
        {
            while (query.Step() == SqliteStepResult.Row) referenced.Add(query.GetText(0).Replace('\\', '/'));
        }

        int deleted = 0;
        if (!Directory.Exists(_options.ArtifactRoot)) return Task.FromResult(0);
        foreach (string file in Directory.EnumerateFiles(_options.ArtifactRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_options.ArtifactRoot, file).Replace('\\', '/');
            if (!referenced.Contains(relative))
            {
                File.Delete(file);
                deleted++;
            }
        }
        return Task.FromResult(deleted);
    }

    public async Task<EvidenceHealthResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        string integrity;
        using (SqliteConnection connection = SqliteConnection.Open(_options.DatabasePath, _options.BusyTimeoutMilliseconds))
        using (SqliteStatement query = connection.Prepare("PRAGMA integrity_check;"))
        {
            integrity = query.Step() == SqliteStepResult.Row ? query.GetText(0) : "no-result";
        }
        AuditVerificationResult audit = await _audit.VerifyAsync(1, cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) reasons.Add(FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED);
        if (!audit.IsValid) reasons.Add(FoundationReasonCodes.AUDIT_CHAIN_BROKEN);
        return new EvidenceHealthResult(reasons.Count == 0, integrity, audit.CheckedEvents, audit.HeadHash, reasons);
    }

    public ValueTask DisposeAsync()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;
        _initialized = false;
        return ValueTask.CompletedTask;
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Evidence session is not initialized.");
    }

    private static void InsertSnapshot(SqliteConnection connection, RuntimeConfigurationSnapshot snapshot, byte[] snapshotJson)
    {
        using SqliteStatement insert = connection.Prepare(
            "INSERT INTO runtime_snapshots(snapshot_id,snapshot_json,snapshot_sha256,created_at_utc) VALUES(?1,?2,?3,?4);");
        insert.BindText(1, snapshot.SnapshotId);
        insert.BindText(2, System.Text.Encoding.UTF8.GetString(snapshotJson));
        insert.BindText(3, snapshot.SnapshotSha256);
        insert.BindText(4, snapshot.CreatedAtUtc);
        insert.ExecuteDone();
    }

    private static ArtifactDescriptor ResolveOrInsertArtifact(SqliteConnection connection, ArtifactDescriptor artifact)
    {
        using (SqliteStatement query = connection.Prepare(
            "SELECT artifact_id,media_type,sha256,size_bytes,relative_path,classification,created_at_utc FROM artifacts WHERE sha256=?1;"))
        {
            query.BindText(1, artifact.Sha256);
            if (query.Step() == SqliteStepResult.Row)
            {
                return new ArtifactDescriptor(query.GetText(0), query.GetText(1), query.GetText(2), query.GetInt64(3), query.GetText(4), query.GetText(5), query.GetText(6));
            }
        }
        using SqliteStatement insert = connection.Prepare(
            "INSERT INTO artifacts(artifact_id,media_type,sha256,size_bytes,relative_path,classification,created_at_utc) VALUES(?1,?2,?3,?4,?5,?6,?7);");
        insert.BindText(1, artifact.ArtifactId);
        insert.BindText(2, artifact.MediaType);
        insert.BindText(3, artifact.Sha256);
        insert.BindInt64(4, artifact.SizeBytes);
        insert.BindText(5, artifact.RelativePath);
        insert.BindText(6, artifact.Classification);
        insert.BindText(7, artifact.CreatedAtUtc);
        insert.ExecuteDone();
        return artifact;
    }

    private static ObservationRecord InsertObservation(
        SqliteConnection connection,
        EvidenceFinalizationRequest request,
        ArtifactDescriptor artifact,
        string auditHead)
    {
        using SqliteStatement insert = connection.Prepare(
            "INSERT INTO observations(observation_id,operation_id,request_id,trace_id,status,input_sha256,canonical_context_sha256,runtime_snapshot_id,output_artifact_id,audit_head,idempotency_key,created_at_utc,updated_at_utc) " +
            "VALUES(?1,?2,?3,?4,'COMPLETED',?5,?6,?7,?8,?9,?10,?11,?11);");
        insert.BindText(1, request.ObservationId);
        insert.BindText(2, request.OperationId);
        insert.BindText(3, request.RequestId);
        insert.BindText(4, request.TraceId);
        insert.BindText(5, request.InputSha256);
        insert.BindNullableText(6, request.CanonicalContextSha256);
        insert.BindText(7, request.RuntimeSnapshot.SnapshotId);
        insert.BindText(8, artifact.ArtifactId);
        insert.BindText(9, auditHead);
        insert.BindText(10, request.IdempotencyKey);
        insert.BindText(11, request.CreatedAtUtc);
        insert.ExecuteDone();

        JsonElement outputRef = JsonSerializer.SerializeToElement(new
        {
            artifact_id = artifact.ArtifactId,
            media_type = artifact.MediaType,
            sha256 = artifact.Sha256,
            size_bytes = artifact.SizeBytes,
            relative_path = artifact.RelativePath,
            classification = artifact.Classification,
        });
        return new ObservationRecord
        {
            Contract = "fs-observer/observation-record/1",
            ObservationId = request.ObservationId,
            OperationId = request.OperationId,
            RequestId = request.RequestId,
            TraceId = request.TraceId,
            Status = "COMPLETED",
            InputSha256 = request.InputSha256,
            CanonicalContextSha256 = request.CanonicalContextSha256,
            RuntimeSnapshotId = request.RuntimeSnapshot.SnapshotId,
            OutputRef = outputRef,
            AuditHead = auditHead,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAtUtc = request.CreatedAtUtc,
            UpdatedAtUtc = request.CreatedAtUtc,
        };
    }

    private static void CompleteIdempotency(SqliteConnection connection, EvidenceFinalizationRequest request)
    {
        using SqliteStatement update = connection.Prepare(
            "UPDATE idempotency_records SET observation_id=?1,state='COMPLETED',completed_at_utc=?2 " +
            "WHERE idempotency_key=?3 AND request_fingerprint=?4 AND operation_id=?5 AND state='RESERVED';");
        update.BindText(1, request.ObservationId);
        update.BindText(2, request.CreatedAtUtc);
        update.BindText(3, request.IdempotencyKey);
        update.BindText(4, request.RequestFingerprint);
        update.BindText(5, request.OperationId);
        update.ExecuteDone();
        if (connection.Changes != 1)
        {
            throw new EvidenceStoreException(FoundationReasonCodes.REQ_IDEMPOTENCY_CONFLICT, "Idempotency reservation is missing or conflicts with the finalization request.");
        }
    }

    private static void CompleteOperation(SqliteConnection connection, string operationId, string completedAtUtc)
    {
        using SqliteStatement update = connection.Prepare(
            "UPDATE operations SET state='COMPLETED',reason_json='[]',updated_at_utc=?1,completed_at_utc=?1 WHERE operation_id=?2 AND state='PERSISTING';");
        update.BindText(1, completedAtUtc);
        update.BindText(2, operationId);
        update.ExecuteDone();
        if (connection.Changes != 1)
        {
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_WRITE_FAILED, "Operation was not in PERSISTING state during finalization.");
        }
    }
}
