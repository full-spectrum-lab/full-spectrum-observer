using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Application;

public sealed record EvidenceOptions
{
    public required string DataDirectory { get; init; }
    public int BusyTimeoutMilliseconds { get; init; } = 5_000;
    public string DatabaseFileName { get; init; } = "observer.db";

    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDirectory), DatabaseFileName);
    public string ArtifactRoot => Path.Combine(Path.GetFullPath(DataDirectory), "artifacts");
    public string TemporaryRoot => Path.Combine(Path.GetFullPath(DataDirectory), "temp");
    public string InstanceLockPath => Path.Combine(Path.GetFullPath(DataDirectory), "observer.instance.lock");
}

public sealed record OperationCreateRequest(
    string OperationId,
    string RequestId,
    string TraceId,
    string State,
    int TimeoutSeconds,
    string UpdatedAtUtc);

public sealed record OperationRecord(
    string OperationId,
    string RequestId,
    string TraceId,
    string State,
    string ReasonJson,
    string? StartedAtUtc,
    string UpdatedAtUtc,
    string? CompletedAtUtc,
    int TimeoutSeconds);

public sealed record ArtifactWriteRequest(
    ReadOnlyMemory<byte> Content,
    string MediaType,
    string Classification);

public sealed record ArtifactDescriptor(
    string ArtifactId,
    string MediaType,
    string Sha256,
    long SizeBytes,
    string RelativePath,
    string Classification,
    string CreatedAtUtc);

public enum IdempotencyReservationState
{
    Reserved,
    ExistingInProgress,
    ExistingCompleted,
    Conflict,
}

public sealed record IdempotencyReservationResult(
    IdempotencyReservationState State,
    string OperationId,
    string? ObservationId,
    string? ReasonCode);

public sealed record EvidenceFinalizationRequest(
    string OperationId,
    string RequestId,
    string ObservationId,
    string TraceId,
    string IdempotencyKey,
    string RequestFingerprint,
    string InputSha256,
    string? CanonicalContextSha256,
    RuntimeConfigurationSnapshot RuntimeSnapshot,
    ReadOnlyMemory<byte> EngineOutputBytes,
    string EngineOutputMediaType,
    string Classification,
    JsonElement AuditActor,
    string AuditEventType,
    string AuditPayloadDigest,
    string CreatedAtUtc);

public sealed record EvidenceFinalizationResult(
    ArtifactDescriptor OutputArtifact,
    ObservationRecord Observation,
    AuditEventContract AuditEvent);

public sealed record AuditVerificationResult(
    bool IsValid,
    long CheckedEvents,
    long? FirstBrokenSequence,
    string? ReasonCode,
    string HeadHash);

public sealed record EvidenceHealthResult(
    bool IsHealthy,
    string IntegrityCheck,
    long AuditEventCount,
    string AuditHead,
    IReadOnlyList<string> ReasonCodes);

public interface IEvidenceSession : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<EvidenceFinalizationResult> FinalizeAsync(EvidenceFinalizationRequest request, CancellationToken cancellationToken);
    Task<int> CleanupOrphanArtifactsAsync(CancellationToken cancellationToken);
    Task<EvidenceHealthResult> CheckHealthAsync(CancellationToken cancellationToken);
}

public interface IOperationStore
{
    Task CreateAsync(OperationCreateRequest request, CancellationToken cancellationToken);
    Task UpdateStateAsync(string operationId, string state, string reasonJson, string updatedAtUtc, string? completedAtUtc, CancellationToken cancellationToken);
    Task<OperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken);
}

public interface IObservationRepository
{
    Task<ObservationRecord?> GetAsync(string observationId, CancellationToken cancellationToken);
}

public interface IRuntimeSnapshotRepository
{
    Task<RuntimeConfigurationSnapshot?> GetAsync(string snapshotId, CancellationToken cancellationToken);
}

public interface IAuditWriter
{
    Task<AuditVerificationResult> VerifyAsync(long fromSequence, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactDescriptor> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    Task<IdempotencyReservationResult> ReserveAsync(string idempotencyKey, string requestFingerprint, string operationId, string createdAtUtc, CancellationToken cancellationToken);
}
