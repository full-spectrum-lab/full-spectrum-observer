using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Application;

public sealed record FoundationExecutionOptions
{
    public required string RepositoryRoot { get; init; }
    public required string SchemaDirectory { get; init; }
    public required string CasePackDirectory { get; init; }
    public required string AllowedInputRoot { get; init; }
    public required string DataDirectory { get; init; }
    public int DefaultTimeoutSeconds { get; init; } = 30;
    public int MaximumInputBytes { get; init; } = 1024 * 1024;

    public void Validate()
    {
        string[] directories = [RepositoryRoot, SchemaDirectory, CasePackDirectory, AllowedInputRoot];
        if (directories.Any(static path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Execution paths must be absolute.");
        if (!Directory.Exists(RepositoryRoot) || !Directory.Exists(SchemaDirectory) || !Directory.Exists(CasePackDirectory))
            throw new DirectoryNotFoundException("Repository, Schema, or Case Pack directory is missing.");
        if (DefaultTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeoutSeconds));
        if (MaximumInputBytes <= 0 || MaximumInputBytes > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumInputBytes));
    }
}

public sealed record FoundationAnalysisResult(
    int ExitCode,
    FoundationOperationStatus Operation,
    ValidationResultContract SchemaValidation,
    ValidationResultContract? GovernanceValidation,
    GovernanceOutputEnvelope? Output,
    ObservationRecord? Observation,
    FoundationErrorEnvelope? Error);

public sealed record FoundationObservationView(
    ObservationRecord Observation,
    RuntimeConfigurationSnapshot? RuntimeSnapshot,
    JsonElement? EngineOutput);

public sealed record FoundationHealthResult(
    bool IsHealthy,
    string Status,
    IReadOnlyList<string> ReasonCodes,
    EvidenceHealthResult? Evidence,
    bool EngineConfigurationReady);

public interface IAnalyzeUseCase
{
    Task<FoundationAnalysisResult> AnalyzeAsync(
        FoundationAnalysisRequest request,
        CancellationToken cancellationToken);
}

public interface IShowObservationUseCase
{
    Task<FoundationObservationView?> ShowAsync(
        string observationId,
        CancellationToken cancellationToken);
}

public interface IVerifyAuditUseCase
{
    Task<AuditVerificationResult> VerifyAsync(
        long fromSequence,
        CancellationToken cancellationToken);
}

public interface IFoundationHealthUseCase
{
    Task<FoundationHealthResult> CheckAsync(CancellationToken cancellationToken);
}
