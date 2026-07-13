using FullSpectrum.Observer.Application;

namespace FullSpectrum.Observer.Execution;

public sealed record ExecutionUseCases(
    IAnalyzeUseCase Analyze,
    IShowObservationUseCase Show,
    IVerifyAuditUseCase VerifyAudit,
    IFoundationHealthUseCase Health);

public sealed record EvidenceComponentsPort(
    IEvidenceSession Session,
    IOperationStore Operations,
    IObservationRepository Observations,
    IRuntimeSnapshotRepository RuntimeSnapshots,
    IAuditWriter Audit,
    IArtifactStore Artifacts,
    IIdempotencyStore Idempotency);

public static class ExecutionComposition
{
    public static ExecutionUseCases Create(
        FoundationExecutionOptions options,
        EvidenceComponentsPort evidence,
        IObserverEngineFacade engine,
        IClock clock,
        IIdGenerator ids,
        Func<bool> engineReady)
    {
        var reads = new FoundationReadUseCases(
            evidence.Session,
            evidence.Observations,
            evidence.RuntimeSnapshots,
            evidence.Artifacts,
            evidence.Audit,
            engineReady);
        return new ExecutionUseCases(
            new FoundationAnalysisUseCase(
                options,
                evidence.Session,
                evidence.Operations,
                evidence.Observations,
                evidence.Idempotency,
                engine,
                clock,
                ids),
            reads,
            reads,
            reads);
    }
}
