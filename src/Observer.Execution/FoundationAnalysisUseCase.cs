using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Execution;

public sealed class FoundationAnalysisUseCase : IAnalyzeUseCase
{
    private readonly FoundationExecutionOptions _options;
    private readonly IEvidenceSession _session;
    private readonly IOperationStore _operations;
    private readonly IObservationRepository _observations;
    private readonly IIdempotencyStore _idempotency;
    private readonly IObserverEngineFacade _engine;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly FoundationInputIntake _intake;
    private readonly FoundationScenarioAdapter _adapter;
    private readonly FoundationValidationPipeline _validation;
    private readonly RuntimeConfigurationResolver _snapshotResolver;
    private readonly GovernanceOutputAssembler _assembler;

    public FoundationAnalysisUseCase(
        FoundationExecutionOptions options,
        IEvidenceSession session,
        IOperationStore operations,
        IObservationRepository observations,
        IIdempotencyStore idempotency,
        IObserverEngineFacade engine,
        IClock clock,
        IIdGenerator ids)
    {
        options.Validate();
        _options = options;
        _session = session;
        _operations = operations;
        _observations = observations;
        _idempotency = idempotency;
        _engine = engine;
        _clock = clock;
        _ids = ids;
        _intake = new FoundationInputIntake(options);
        _adapter = new FoundationScenarioAdapter();
        _validation = new FoundationValidationPipeline(options, clock);
        _snapshotResolver = new RuntimeConfigurationResolver(options, clock, ids);
        _assembler = new GovernanceOutputAssembler(options, clock);
    }

    public async Task<FoundationAnalysisResult> AnalyzeAsync(FoundationAnalysisRequest request, CancellationToken cancellationToken)
    {
        await _session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        string traceId = _ids.NewId().ToString("D");
        string operationId = _ids.NewId().ToString("D");
        string now = _clock.UtcNow.ToString("O");
        int timeout = checked((int)(request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds));
        byte[] requestBytes = FoundationJson.Serialize(request);
        string requestFingerprint = FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(requestBytes));

        IdempotencyReservationResult reservation = await _idempotency.ReserveAsync(
            request.IdempotencyKey, requestFingerprint, operationId, now, cancellationToken).ConfigureAwait(false);
        if (reservation.State == IdempotencyReservationState.Conflict)
            return FailureWithoutOperation(request, traceId, FoundationReasonCodes.REQ_IDEMPOTENCY_CONFLICT, "REQUEST");
        if (reservation.State is IdempotencyReservationState.ExistingCompleted or IdempotencyReservationState.ExistingInProgress)
            return await ExistingAsync(request, traceId, timeout, reservation, cancellationToken).ConfigureAwait(false);

        await _operations.CreateAsync(new OperationCreateRequest(
            operationId, request.RequestId, traceId, OperationStates.Received, timeout, now), cancellationToken).ConfigureAwait(false);

        ValidationResultContract schemaValidation = _validation.ValidateRequest(request);
        if (schemaValidation.Status != "PASS")
        {
            await TransitionAsync(operationId, OperationStates.RejectedSchema, schemaValidation.ReasonCodes.GetRawText(), true, cancellationToken).ConfigureAwait(false);
            return await FailureAsync(request, operationId, traceId, schemaValidation, null,
                FoundationReasonCodes.SCHEMA_TYPE_INVALID, "SCHEMA", 10, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await TransitionAsync(operationId, OperationStates.Adapting, "[]", false, cancellationToken).ConfigureAwait(false);
            IntakeResult intake = await _intake.LoadAsync(request.Input, cancellationToken).ConfigureAwait(false);
            AdapterResult adapted = _adapter.Adapt(intake.Scenario);

            await TransitionAsync(operationId, OperationStates.ValidatingSchema, "[]", false, cancellationToken).ConfigureAwait(false);
            await TransitionAsync(operationId, OperationStates.ValidatingGovernance, "[]", false, cancellationToken).ConfigureAwait(false);
            (ValidationResultContract governanceValidation, GovernanceValidationSummary summary) = _validation.ValidateGovernance(adapted.CanonicalContext);
            if (!summary.IsSufficient)
            {
                await TransitionAsync(operationId, OperationStates.NeedsEvidence, governanceValidation.ReasonCodes.GetRawText(), true, cancellationToken).ConfigureAwait(false);
                return await FailureAsync(request, operationId, traceId, schemaValidation, governanceValidation,
                    FoundationReasonCodes.GOV_CONTEXT_INSUFFICIENT, "GOVERNANCE", 10, cancellationToken).ConfigureAwait(false);
            }

            RuntimeConfigurationSnapshot snapshot = _snapshotResolver.Resolve();
            await TransitionAsync(operationId, OperationStates.SnapshotFixed, "[]", false, cancellationToken).ConfigureAwait(false);

            EngineFacadeRequest engineRequest = new()
            {
                Protocol = "fs-observer-engine-facade/1",
                RequestId = request.RequestId,
                Operation = "evaluate",
                Engine = JsonSerializer.SerializeToElement(new
                {
                    version = BuildIdentity.EngineVersion,
                    commit = BuildIdentity.EngineCommit,
                }, FoundationJson.CreateOptions()),
                Seed = request.RequestedRuntime.GetProperty("seed").GetInt64(),
                FixedTimeUtc = request.RequestedRuntime.GetProperty("fixed_time_utc").GetString()
                    ?? throw new InvalidDataException("fixed_time_utc is missing."),
                Scenario = adapted.CanonicalContext.Clone(),
                OutputSerialization = "FSE-PYJSON-1",
            };

            await TransitionAsync(operationId, OperationStates.EngineRunning, "[]", false, cancellationToken).ConfigureAwait(false);
            EngineFacadeExecutionResult engineResult = await _engine.EvaluateAsync(
                engineRequest, TimeSpan.FromSeconds(timeout), cancellationToken).ConfigureAwait(false);
            if (engineResult.Response.Status == "CANCELLED")
            {
                await TransitionAsync(operationId, OperationStates.Cancelling, "[]", false, CancellationToken.None).ConfigureAwait(false);
                await TransitionAsync(operationId, OperationStates.Cancelled, ReasonJson(FoundationReasonCodes.ENGINE_CANCELLED), true, CancellationToken.None).ConfigureAwait(false);
                return await FailureAsync(request, operationId, traceId, schemaValidation, governanceValidation,
                    FoundationReasonCodes.ENGINE_CANCELLED, "ENGINE", 60, CancellationToken.None).ConfigureAwait(false);
            }
            if (engineResult.Response.Status == "TIMED_OUT")
            {
                await TransitionAsync(operationId, OperationStates.TimedOut, ReasonJson(FoundationReasonCodes.ENGINE_TIMEOUT), true, CancellationToken.None).ConfigureAwait(false);
                return await FailureAsync(request, operationId, traceId, schemaValidation, governanceValidation,
                    FoundationReasonCodes.ENGINE_TIMEOUT, "ENGINE", 60, CancellationToken.None).ConfigureAwait(false);
            }
            if (engineResult.Response.Status != "SUCCESS" || engineResult.Response.Output is null)
            {
                string engineReason = ExtractEngineReasonCode(engineResult.Response)
                    ?? FoundationReasonCodes.ENGINE_SIMULATION_ERROR;
                await TransitionAsync(operationId, OperationStates.EngineFailed, ReasonJson(engineReason), true, cancellationToken).ConfigureAwait(false);
                return await FailureAsync(request, operationId, traceId, schemaValidation, governanceValidation,
                    engineReason, StageFor(engineReason), ExitCodeFor(engineReason), cancellationToken).ConfigureAwait(false);
            }

            await TransitionAsync(operationId, OperationStates.AssemblingOutput, "[]", false, cancellationToken).ConfigureAwait(false);
            byte[] engineOutputBytes = Encoding.UTF8.GetBytes(engineResult.Response.Output.Value.GetRawText());
            string actualOutputDigest = Convert.ToHexStringLower(SHA256.HashData(engineOutputBytes));
            if (!string.Equals(actualOutputDigest, engineResult.Response.OutputSha256, StringComparison.Ordinal))
                throw new OutputAssemblyException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH, "Engine output digest mismatch.");

            string observationId = _ids.NewId().ToString("D");
            await TransitionAsync(operationId, OperationStates.Persisting, "[]", false, cancellationToken).ConfigureAwait(false);
            EvidenceFinalizationResult evidence = await _session.FinalizeAsync(new EvidenceFinalizationRequest(
                operationId, request.RequestId, observationId, traceId, request.IdempotencyKey,
                requestFingerprint, intake.InputSha256, adapted.CanonicalContextSha256, snapshot,
                engineOutputBytes, "application/json", "SYNTHETIC",
                JsonSerializer.SerializeToElement(new { actor_type = "SYSTEM", actor_id = "foundation-analysis" }),
                "OBSERVATION_COMPLETED", engineResult.Response.OutputSha256!, _clock.UtcNow.ToString("O")),
                cancellationToken).ConfigureAwait(false);

            GovernanceOutputEnvelope output = _assembler.Assemble(observationId, traceId, snapshot, engineResult.Response, evidence);
            OperationRecord completed = await _operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed operation is missing.");
            return new FoundationAnalysisResult(0, ToStatus(completed, observationId), schemaValidation,
                governanceValidation, output, evidence.Observation, null);
        }
        catch (OperationCanceledException)
        {
            await TryCancellationTransitionAsync(operationId).ConfigureAwait(false);
            return await FailureAsync(request, operationId, traceId, schemaValidation, null,
                FoundationReasonCodes.ENGINE_CANCELLED, "ENGINE", 60, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IReasonCodedException)
        {
            string code = ((IReasonCodedException)exception).ReasonCode;
            await TryTerminalTransitionAsync(operationId, TerminalFor(code), code).ConfigureAwait(false);
            return await FailureAsync(request, operationId, traceId, schemaValidation, null,
                code, StageFor(code), ExitCodeFor(code), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await TryTerminalTransitionAsync(operationId, OperationStates.InternalFailed, FoundationReasonCodes.SYSTEM_INTERNAL_ERROR).ConfigureAwait(false);
            return await FailureAsync(request, operationId, traceId, schemaValidation, null,
                FoundationReasonCodes.SYSTEM_INTERNAL_ERROR, "SYSTEM", 70, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<FoundationAnalysisResult> ExistingAsync(
        FoundationAnalysisRequest request, string traceId, int timeout,
        IdempotencyReservationResult reservation, CancellationToken cancellationToken)
    {
        ObservationRecord? existing = reservation.ObservationId is null
            ? null
            : await _observations.GetAsync(reservation.ObservationId, cancellationToken).ConfigureAwait(false);
        OperationRecord? operation = await _operations.GetAsync(reservation.OperationId, cancellationToken).ConfigureAwait(false);
        OperationRecord fallback = operation ?? new OperationRecord(
            reservation.OperationId, request.RequestId, traceId,
            reservation.State == IdempotencyReservationState.ExistingCompleted ? OperationStates.Completed : OperationStates.Received,
            "[]", null, _clock.UtcNow.ToString("O"),
            reservation.State == IdempotencyReservationState.ExistingCompleted ? _clock.UtcNow.ToString("O") : null,
            timeout);
        return new FoundationAnalysisResult(0, ToStatus(fallback, existing?.ObservationId),
            EmptyValidation("SCHEMA", "PASS"), null, null, existing, null);
    }

    private async Task TransitionAsync(string operationId, string state, string reasons, bool terminal, CancellationToken cancellationToken)
    {
        string now = _clock.UtcNow.ToString("O");
        await _operations.UpdateStateAsync(operationId, state, reasons, now, terminal ? now : null, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCancellationTransitionAsync(string operationId)
    {
        try
        {
            OperationRecord? current = await _operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || OperationStateMachine.IsTerminal(current.State)) return;
            if (OperationStateMachine.CanTransition(current.State, OperationStates.Cancelling))
            {
                await TransitionAsync(operationId, OperationStates.Cancelling, "[]", false, CancellationToken.None).ConfigureAwait(false);
                await TransitionAsync(operationId, OperationStates.Cancelled, ReasonJson(FoundationReasonCodes.ENGINE_CANCELLED), true, CancellationToken.None).ConfigureAwait(false);
            }
            else if (OperationStateMachine.CanTransition(current.State, OperationStates.InternalFailed))
            {
                await TransitionAsync(operationId, OperationStates.InternalFailed, ReasonJson(FoundationReasonCodes.ENGINE_CANCELLED), true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cancellation reporting must not hide the original cancellation.
        }
    }

    private async Task TryTerminalTransitionAsync(string operationId, string state, string reasonCode)
    {
        try
        {
            OperationRecord? current = await _operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || OperationStateMachine.IsTerminal(current.State)) return;
            if (OperationStateMachine.CanTransition(current.State, state))
                await TransitionAsync(operationId, state, ReasonJson(reasonCode), true, CancellationToken.None).ConfigureAwait(false);
            else if (OperationStateMachine.CanTransition(current.State, OperationStates.InternalFailed))
                await TransitionAsync(operationId, OperationStates.InternalFailed, ReasonJson(reasonCode), true, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<FoundationAnalysisResult> FailureAsync(
        FoundationAnalysisRequest request, string operationId, string traceId,
        ValidationResultContract schemaValidation, ValidationResultContract? governanceValidation,
        string reasonCode, string stage, int exitCode, CancellationToken cancellationToken)
    {
        OperationRecord record = await _operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? new OperationRecord(operationId, request.RequestId, traceId, OperationStates.InternalFailed,
                ReasonJson(reasonCode), null, _clock.UtcNow.ToString("O"), _clock.UtcNow.ToString("O"),
                checked((int)(request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds)));
        return new FoundationAnalysisResult(exitCode, ToStatus(record, null), schemaValidation, governanceValidation,
            null, null, Error(traceId, stage, reasonCode));
    }

    private FoundationAnalysisResult FailureWithoutOperation(FoundationAnalysisRequest request, string traceId, string reasonCode, string stage)
    {
        string now = _clock.UtcNow.ToString("O");
        FoundationOperationStatus status = new()
        {
            Contract = "fs-observer/foundation-operation-status/1",
            OperationId = _ids.NewId().ToString("D"), RequestId = request.RequestId,
            ObservationId = null, TraceId = traceId, State = OperationStates.InternalFailed,
            ProgressStage = stage, StartedAtUtc = now, UpdatedAtUtc = now, CompletedAtUtc = now,
            ReasonCodes = ReasonElement(reasonCode),
        };
        return new FoundationAnalysisResult(10, status, EmptyValidation("SCHEMA", "FAIL"), null,
            null, null, Error(traceId, stage, reasonCode));
    }

    private FoundationErrorEnvelope Error(string traceId, string stage, string reasonCode) => new()
    {
        Contract = "fs-observer/foundation-error-envelope/1",
        ErrorId = _ids.NewId().ToString("D"), TraceId = traceId, Stage = stage,
        ReasonCodes = ReasonElement(reasonCode), OccurredAtUtc = _clock.UtcNow.ToString("O"),
        Details = JsonSerializer.SerializeToElement(new { details_redacted = true }),
    };

    private static FoundationOperationStatus ToStatus(OperationRecord record, string? observationId) => new()
    {
        Contract = "fs-observer/foundation-operation-status/1",
        OperationId = record.OperationId, RequestId = record.RequestId, ObservationId = observationId,
        TraceId = record.TraceId, State = record.State, ProgressStage = record.State,
        StartedAtUtc = record.StartedAtUtc, UpdatedAtUtc = record.UpdatedAtUtc,
        CompletedAtUtc = record.CompletedAtUtc, ReasonCodes = ParseReasonElement(record.ReasonJson),
    };

    private ValidationResultContract EmptyValidation(string kind, string status) => new()
    {
        Contract = "fs-observer/validation-result/1", ValidationKind = kind, Status = status,
        SchemaRef = null, ReasonCodes = JsonSerializer.SerializeToElement(Array.Empty<object>()),
        KnownFields = JsonSerializer.SerializeToElement(Array.Empty<string>()),
        UnknownFields = JsonSerializer.SerializeToElement(Array.Empty<string>()),
        ValidatedAtUtc = _clock.UtcNow.ToString("O"),
    };

    private static JsonElement ParseReasonElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        return document.RootElement.Clone();
    }

    private static JsonElement ReasonElement(string code) => JsonSerializer.SerializeToElement(new[]
    {
        new { code, message = "See reason_code catalog.", retryable = false, details_redacted = true }
    });
    private static string ReasonJson(string code) => ReasonElement(code).GetRawText();

    private static string? ExtractEngineReasonCode(EngineFacadeResponse response)
    {
        if (response.Error is not JsonElement error || error.ValueKind != JsonValueKind.Object)
            return null;
        if (!error.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String)
            return null;
        string? value = code.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string TerminalFor(string code)
    {
        if (code.StartsWith("INTAKE_", StringComparison.Ordinal) || code.StartsWith("ADAPTER_", StringComparison.Ordinal)) return OperationStates.AdapterFailed;
        if (code.StartsWith("ENGINE_", StringComparison.Ordinal) || code.StartsWith("FACADE_", StringComparison.Ordinal)) return OperationStates.EngineFailed;
        if (code.StartsWith("STORE_", StringComparison.Ordinal) || code.StartsWith("AUDIT_", StringComparison.Ordinal)) return OperationStates.StoreFailed;
        if (code.StartsWith("SCHEMA_", StringComparison.Ordinal)) return OperationStates.RejectedSchema;
        return OperationStates.InternalFailed;
    }
    private static string StageFor(string code) => code.Split('_', 2)[0];
    private static int ExitCodeFor(string code) => code.Split('_', 2)[0] switch
    {
        "REQ" or "SCHEMA" or "GOV" => 10,
        "INTAKE" or "ADAPTER" => 20,
        "FACADE" or "ENGINE" or "OUTPUT" => 30,
        "STORE" => 40,
        "AUDIT" => 50,
        _ => 70,
    };
}
