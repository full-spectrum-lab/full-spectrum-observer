using FullSpectrum.Observer.Application;

namespace FullSpectrum.Observer.Evidence;

public sealed record EvidenceComponents(
    IEvidenceSession Session,
    IOperationStore Operations,
    IObservationRepository Observations,
    IRuntimeSnapshotRepository RuntimeSnapshots,
    IAuditWriter Audit,
    IArtifactStore Artifacts,
    IIdempotencyStore Idempotency);

public static class EvidenceComposition
{
    public static EvidenceComponents Create(EvidenceOptions options, IClock? clock = null, IIdGenerator? ids = null)
    {
        clock ??= new SystemClock();
        ids ??= new GuidIdGenerator();
        var artifacts = new ContentAddressedArtifactStore(options, clock, ids);
        var audit = new SqliteAuditWriter(options, clock, ids);
        return new EvidenceComponents(
            new SqliteEvidenceSession(options, clock, artifacts, audit),
            new SqliteOperationStore(options),
            new SqliteObservationRepository(options),
            new SqliteRuntimeSnapshotRepository(options),
            audit,
            artifacts,
            new SqliteIdempotencyStore(options));
    }
}
