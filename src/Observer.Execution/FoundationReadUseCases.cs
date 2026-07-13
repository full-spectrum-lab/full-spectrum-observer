using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Execution;

public sealed class FoundationReadUseCases :
    IShowObservationUseCase,
    IVerifyAuditUseCase,
    IFoundationHealthUseCase
{
    private readonly IEvidenceSession _session;
    private readonly IObservationRepository _observations;
    private readonly IRuntimeSnapshotRepository _snapshots;
    private readonly IArtifactStore _artifacts;
    private readonly IAuditWriter _audit;
    private readonly Func<bool> _engineReady;

    public FoundationReadUseCases(
        IEvidenceSession session,
        IObservationRepository observations,
        IRuntimeSnapshotRepository snapshots,
        IArtifactStore artifacts,
        IAuditWriter audit,
        Func<bool> engineReady)
    {
        _session = session;
        _observations = observations;
        _snapshots = snapshots;
        _artifacts = artifacts;
        _audit = audit;
        _engineReady = engineReady;
    }

    public async Task<FoundationObservationView?> ShowAsync(string observationId, CancellationToken cancellationToken)
    {
        await _session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ObservationRecord? observation = await _observations.GetAsync(observationId, cancellationToken).ConfigureAwait(false);
        if (observation is null) return null;

        RuntimeConfigurationSnapshot? snapshot = await _snapshots.GetAsync(observation.RuntimeSnapshotId, cancellationToken).ConfigureAwait(false);
        JsonElement? output = null;
        if (observation.OutputRef is JsonElement outputRef && outputRef.ValueKind == JsonValueKind.Object)
        {
            ArtifactDescriptor descriptor = new(
                outputRef.GetProperty("artifact_id").GetString()!,
                outputRef.GetProperty("media_type").GetString()!,
                outputRef.GetProperty("sha256").GetString()!,
                outputRef.GetProperty("size_bytes").GetInt64(),
                outputRef.GetProperty("relative_path").GetString()!,
                outputRef.GetProperty("classification").GetString()!,
                outputRef.TryGetProperty("created_at_utc", out JsonElement created) ? created.GetString() ?? string.Empty : string.Empty);
            ReadOnlyMemory<byte> bytes = await _artifacts.ReadAsync(descriptor, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            output = document.RootElement.Clone();
        }
        return new FoundationObservationView(observation, snapshot, output);
    }

    public async Task<AuditVerificationResult> VerifyAsync(long fromSequence, CancellationToken cancellationToken)
    {
        await _session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _audit.VerifyAsync(fromSequence, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            EvidenceHealthResult evidence = await _session.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            bool engineReady = _engineReady();
            var reasons = new List<string>(evidence.ReasonCodes);
            if (!engineReady) reasons.Add(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING);
            return new FoundationHealthResult(
                evidence.IsHealthy && engineReady,
                evidence.IsHealthy && engineReady ? "HEALTHY" : "DEGRADED",
                reasons,
                evidence,
                engineReady);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new FoundationHealthResult(
                false,
                "UNHEALTHY",
                [FoundationReasonCodes.SYSTEM_INTERNAL_ERROR],
                null,
                false);
        }
    }
}
