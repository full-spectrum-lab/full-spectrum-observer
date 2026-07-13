using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Execution;

public sealed class GovernanceOutputAssembler
{
    private readonly FoundationExecutionOptions _options;
    private readonly IClock _clock;

    public GovernanceOutputAssembler(FoundationExecutionOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public GovernanceOutputEnvelope Assemble(
        string observationId,
        string traceId,
        RuntimeConfigurationSnapshot snapshot,
        EngineFacadeResponse engineResponse,
        EvidenceFinalizationResult evidence)
    {
        if (engineResponse.Output is null || string.IsNullOrWhiteSpace(engineResponse.OutputSha256))
            throw new OutputAssemblyException(FoundationReasonCodes.ENGINE_OUTPUT_INVALID, "Engine output is missing.");

        JsonElement artifactRef = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                artifact_id = evidence.OutputArtifact.ArtifactId,
                media_type = evidence.OutputArtifact.MediaType,
                sha256 = evidence.OutputArtifact.Sha256,
                size_bytes = evidence.OutputArtifact.SizeBytes,
                relative_path = evidence.OutputArtifact.RelativePath,
                classification = evidence.OutputArtifact.Classification,
            }
        }, FoundationJson.CreateOptions());

        GovernanceOutputEnvelope envelope = new()
        {
            Contract = "fs-observer/governance-output-envelope/1",
            ObservationId = observationId,
            TraceId = traceId,
            Classification = "SYNTHETIC",
            Boundary = JsonSerializer.SerializeToElement(new
            {
                observer_only = true,
                certified = false,
                authorized = false,
                active_external = false,
            }, FoundationJson.CreateOptions()),
            Engine = snapshot.Engine.Clone(),
            RuntimeSnapshotRef = JsonSerializer.SerializeToElement(new
            {
                id = snapshot.SnapshotId,
                version = "1.0.0-alpha.1",
                schema_version = "1.0.0",
                sha256 = snapshot.SnapshotSha256,
                source_commit = BuildIdentity.EngineCommit,
            }, FoundationJson.CreateOptions()),
            EngineOutput = engineResponse.Output.Value.Clone(),
            EngineOutputSha256 = engineResponse.OutputSha256!,
            Unknowns = JsonSerializer.SerializeToElement(Array.Empty<object>()),
            EvidenceRefs = artifactRef,
            AuditHead = evidence.AuditEvent.EventHash,
            CreatedAtUtc = _clock.UtcNow.ToString("O"),
        };
        using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(_options.SchemaDirectory);
        SchemaValidationReport validation = bundle.Validate(
            "governance-output-envelope", FoundationJson.Serialize(envelope));
        if (!validation.IsValid)
            throw new OutputAssemblyException(
                FoundationReasonCodes.OUTPUT_ASSEMBLY_FAILED,
                "Governance Output Envelope failed the frozen Schema.");
        return envelope;
    }
}

public sealed class OutputAssemblyException : IOException, IReasonCodedException
{
    public OutputAssemblyException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
