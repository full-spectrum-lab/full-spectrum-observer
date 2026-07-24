using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// Process invocation of the pinned Engine v1.5.0 via the REAL Engine entry
/// (<c>engine/worker/worker.py</c>, the worker protocol), EXACTLY like the CLI's
/// <see cref="PythonWorkerEngineFacade"/>. Enforces the version binding and the response digest
/// integrity (fail-closed). Does NOT recompute, merge, or downgrade anything — that is the Engine's
/// job. Never forges replay_ref / evidence digests: the runtime_digest / replay_ref.digest /
/// evidence_digest are taken verbatim from the worker's REAL <c>output_sha256</c>.
/// </summary>
public sealed class EngineFacade : IEngineFacade
{
    private readonly EngineFacadeOptions _options;

    public EngineFacade(EngineFacadeOptions options)
    {
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Sends the request envelope to the pinned Engine v1.5.0 worker and returns the validated
    /// v1.5 response envelope. Throws <see cref="VersionBindingException"/> /
    /// <see cref="DependencyMissingException"/> / <see cref="ContractViolationException"/> on any
    /// deviation (never silently downgrades).
    /// </summary>
    public async Task<EngineResponse> AnalyzeAsync(EngineRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Request-side version binding (fail-closed).
        if (!string.Equals(request.EngineVersion, EngineV15Contract.EngineTag, StringComparison.Ordinal))
        {
            throw new VersionBindingException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH,
                $"engine_version must be pinned to {EngineV15Contract.EngineTag}; received '{request.EngineVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(request.EngineCommit) || string.IsNullOrWhiteSpace(request.SchemaDigest))
        {
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING,
                "Engine v1.5.0 commit / schema_digest is not pinned (dependency missing / not replayable).");
        }

        // 2. Engine (worker) availability (single local operator; no network egress).
        if (!File.Exists(_options.PythonExecutablePath)
            || !File.Exists(_options.WorkerScriptPath)
            || !File.Exists(_options.WorkerLockPath)
            || !Directory.Exists(_options.EngineRootPath))
        {
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING,
                "Engine v1.5.0 worker is not available in this environment (python / worker.py / worker.lock.json / engine root missing — dependency missing / not replayable).");
        }

        // 3. Worker integrity lock + frozen-identity binding (mirror PythonWorkerEngineFacade).
        WorkerLockManifest manifest;
        try
        {
            manifest = WorkerIntegrityVerifier.Verify(_options.WorkerLockPath);
        }
        catch (EngineFacadeException exception)
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine Worker integrity verification failed.", exception);
        }
        if (!string.Equals(manifest.EngineVersion, EngineV15Contract.EngineTag, StringComparison.Ordinal)
            || !string.Equals(manifest.EngineCommit, EngineV15Contract.EngineCommit, StringComparison.Ordinal))
        {
            throw new VersionBindingException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH,
                "Worker lock Engine identity does not match the frozen baseline.");
        }

        // 4. Translate the v1.5 EngineRequest into the worker protocol request.
        JsonElement engineIdentity = JsonSerializer.SerializeToElement(new
        {
            version = request.EngineVersion,
            commit = request.EngineCommit,
        });
        var workerRequest = new EngineFacadeRequest
        {
            Protocol = "fs-observer-engine-facade/1",
            RequestId = Guid.NewGuid().ToString(),
            Operation = "evaluate",
            Engine = engineIdentity,
            Seed = ComputeSeed(request.Input.ContentDigest),
            FixedTimeUtc = "2026-07-04T00:00:00Z",
            Scenario = request.Input.CanonicalInput,
            OutputSerialization = "FSE-PYJSON-1",
        };

        // Seed domain guard: reject any out-of-domain seed BEFORE spawning the Worker, so an
        // invalid seed can never surface as ENGINE_SIMULATION_ERROR / RECOVERY_REQUIRED.
        EngineSeedContract.ValidateOrThrow(workerRequest.Seed);

        // 5. Spawn the worker EXACTLY like PythonWorkerEngineFacade.EvaluateAsync.
        byte[] requestBytes = FoundationJson.Serialize(workerRequest);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerScriptPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_options.WorkerScriptPath);
        startInfo.ArgumentList.Add("--engine-root");
        startInfo.ArgumentList.Add(_options.EngineRootPath);
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["PYTHONHASHSEED"] = "0";
        startInfo.Environment.Remove("HTTP_PROXY");
        startInfo.Environment.Remove("HTTPS_PROXY");
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment["NO_PROXY"] = "*";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!WorkerProcessHost.Start(process))
            {
                throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Engine Worker process did not start.");
            }
        }
        catch (DependencyMissingException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Engine Worker process start failed.", exception);
        }

        Task<byte[]> stdoutTask = LimitedStreamReader.ReadAsync(process.StandardOutput.BaseStream, _options.MaximumResponseBytes, FoundationReasonCodes.FACADE_RESPONSE_TOO_LARGE);
        Task<byte[]> stderrTask = LimitedStreamReader.ReadAsync(process.StandardError.BaseStream, _options.MaximumStandardErrorBytes, FoundationReasonCodes.FACADE_RESPONSE_TOO_LARGE);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes("\n"), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stdoutTask).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stderrTask).ConfigureAwait(false);
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Failed to write the request to the Engine Worker.", exception);
        }

        using var timeoutSource = new CancellationTokenSource(_options.DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stdoutTask).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stderrTask).ConfigureAwait(false);
            throw new DependencyMissingException(FoundationReasonCodes.ENGINE_TIMEOUT, "Engine execution timed out (dependency missing / not replayable).");
        }

        byte[] stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);

        byte[] oneLine = NormalizeOneLine(stdout);
        EngineFacadeResponse response;
        try
        {
            response = FoundationJson.Deserialize<EngineFacadeResponse>(oneLine);
        }
        catch (JsonException exception)
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine Worker response is not valid protocol JSON.", exception);
        }
        if (response is null)
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine Worker returned an empty response.");
        }

        // 6. Response-side handling (fail-closed).
        if (!string.Equals(response.Status, "SUCCESS", StringComparison.Ordinal))
        {
            string code = "UNKNOWN";
            string message = "Engine Worker returned a non-SUCCESS status.";
            if (response.Error is { } err)
            {
                if (err.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                {
                    code = c.GetString() ?? code;
                }
                if (err.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                {
                    message = m.GetString() ?? message;
                }
            }
            if (code == "SYSTEM_DEPENDENCY_MISSING")
            {
                throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, message);
            }
            if (code == "ENGINE_VERSION_MISMATCH")
            {
                throw new VersionBindingException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH, message);
            }
            // ENGINE_SIMULATION_ERROR / FACADE_PROTOCOL_INVALID / anything else -> contract violation.
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, $"Engine Worker error ({code}): {message}");
        }

        // SUCCESS: recompute the digest over the worker's REAL output and compare (red line #8).
        if (response.Output is null || string.IsNullOrWhiteSpace(response.OutputSha256))
        {
            throw new ContractViolationException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH, "Engine Worker SUCCESS response is missing output or its digest.");
        }
        byte[] rawOutput = Encoding.UTF8.GetBytes(response.Output.Value.GetRawText());
        string recomputed = Convert.ToHexStringLower(SHA256.HashData(rawOutput));
        if (!string.Equals(recomputed, response.OutputSha256, StringComparison.Ordinal))
        {
            throw new ContractViolationException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH,
                "Engine Worker output SHA-256 does not match the embedded output digest (red line: never forge replay_ref/evidence digests).");
        }

        // 7. Translate the worker response into the v1.5 EngineResponse envelope (honest pass-through).
        return new EngineResponse
        {
            EngineVersion = response.EngineVersion,
            EngineCommit = response.EngineCommit,
            SchemaVersion = EngineV15Contract.SchemaVersion,
            SchemaDigest = EngineV15Contract.SchemaDigest,
            AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
            ProfileVersion = EngineV15Contract.ProfileVersion,
            Conclusion = response.Output,
            ConflictObservations = new List<EngineConflictObservation>(),
            // M3-FIX-04: the Engine v1.5.0 worker does NOT emit an explicit unknown-state
            // completeness signal. We MUST NOT infer KNOWN from a successful Worker run, nor hide
            // any missing-context signal. Per the ADAPTER_POLICY fail-closed rule the analysis
            // defaults to UNKNOWN (the DB CHECK allows UNKNOWN / KNOWN / PARTIAL). A future
            // Engine/Adapter release that supplies an explicit, contract-valid completeness signal
            // would be honoured by UnknownStateContract.FromVerbatimOrFailClosed.
            UnknownState = DetermineUnknownState(),
            HardGate = false,
            RuntimeDigest = response.OutputSha256,
            ReplayRef = new EngineReplayRef { Digest = response.OutputSha256, EngineVersion = response.EngineVersion },
            Evidence = new EngineEvidence { EvidenceDigest = response.OutputSha256, References = new List<string>() },
        };
    }

    /// <summary>
    /// Determines the persisted unknown-state for a SUCCESS Engine response.
    /// </summary>
    /// <remarks>
    /// M3-FIX-04: the Engine v1.5.0 worker output carries NO explicit unknown-state completeness
    /// signal, so we cannot prove the analysis context is fully known. We therefore MUST NOT map a
    /// successful Worker run to <c>KNOWN</c>, and we MUST NOT suppress a missing-context signal.
    /// Per the ADAPTER_POLICY fail-closed rule we default to <see cref="UnknownStateContract.FailClosed"/>
    /// (UNKNOWN). If a future Engine/Adapter release supplies an explicit, contract-valid
    /// completeness signal, it would be honoured by
    /// <see cref="UnknownStateContract.FromVerbatimOrFailClosed"/> here.
    /// </remarks>
    private static string DetermineUnknownState() => UnknownStateContract.FailClosed;

    /// <summary>
    /// Deterministic seed derived from the request content digest (first 8 hex chars).
    /// Delegates to the typed <see cref="EngineSeedContract"/> so the seed is ALWAYS a non-negative
    /// value in the UInt32 domain [0, 4294967295] — never the sign-truncated negative produced by the
    /// legacy <c>unchecked((int)value)</c> logic.
    /// </summary>
    private static long ComputeSeed(string contentDigest)
    {
        long seed = EngineSeedContract.FromContentDigest(contentDigest);
        EngineSeedContract.ValidateOrThrow(seed);
        return seed;
    }

    private async Task TerminateAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }
        using var grace = new CancellationTokenSource(_options.KillGracePeriod);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) { }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ObserveStreamTaskAsync(Task<byte[]> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (EngineFacadeException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private static byte[] NormalizeOneLine(byte[] stdout)
    {
        string text = Encoding.UTF8.GetString(stdout).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine Worker stdout must contain exactly one JSON line.");
        }
        return Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>Composition root for the v1.5 Engine facade.</summary>
public static class EngineV15Composition
{
    public static EngineFacade Create(EngineFacadeOptions options) => new(options);
}
