using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// Process invocation of the pinned Engine v1.5.0 (python -m governance_chain) under a single
/// local operator identity. Enforces the version binding and the response digest integrity
/// (fail-closed). Does NOT recompute, merge, or downgrade anything — that is the Engine's job.
/// </summary>
public sealed class EngineFacade
{
    private readonly EngineV15Options _options;

    public EngineFacade(EngineV15Options options)
    {
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Sends the request envelope to Engine v1.5.0 and returns the validated response envelope.
    /// Throws <see cref="VersionBindingException"/> / <see cref="DependencyMissingException"/> /
    /// <see cref="ContractViolationException"/> on any deviation (never silently downgrades).
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

        // 2. Engine availability (single local operator; no network egress).
        if (!File.Exists(_options.PythonExecutablePath) || !Directory.Exists(_options.EngineRootPath))
        {
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING,
                "Engine v1.5.0 is not available in this environment (dependency missing / not replayable).");
        }

        // 3. Process invocation.
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, EngineV15Contract.EnvelopeOptions);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutablePath,
            WorkingDirectory = Path.GetFullPath(_options.EngineRootPath),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(_options.EngineModule);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Engine process did not start.");
            }
        }
        catch (DependencyMissingException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Engine process start failed.", exception);
        }

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw new DependencyMissingException(FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING, "Failed to send request to the Engine.", exception);
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
            throw new DependencyMissingException(FoundationReasonCodes.ENGINE_TIMEOUT,
                "Engine execution timed out (dependency missing / not replayable).");
        }

        string stdout = await process.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
        _ = await process.StandardError.ReadToEndAsync(linked.Token).ConfigureAwait(false);

        // 4. Response-side validation (fail-closed).
        EngineResponse response;
        try
        {
            response = JsonSerializer.Deserialize<EngineResponse>(NormalizeOneLine(stdout), EngineV15Contract.EnvelopeOptions)
                ?? throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine returned an empty response.");
        }
        catch (ContractViolationException) { throw; }
        catch (JsonException exception)
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine response is not valid JSON.", exception);
        }

        if (!string.Equals(response.EngineVersion, EngineV15Contract.EngineTag, StringComparison.Ordinal))
        {
            throw new ContractViolationException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH,
                $"Response engine_version must be {EngineV15Contract.EngineTag}; received '{response.EngineVersion}'.");
        }
        if (response.ReplayRef is null || string.IsNullOrWhiteSpace(response.ReplayRef.Digest))
        {
            throw new ContractViolationException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH,
                "Response missing replay_ref.digest (red line #8: replay anchor must not be forged).");
        }
        if (response.Evidence is null || string.IsNullOrWhiteSpace(response.Evidence.EvidenceDigest))
        {
            throw new ContractViolationException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH,
                "Response missing evidence.evidence_digest (red line #8).");
        }
        if (string.IsNullOrWhiteSpace(response.RuntimeDigest))
        {
            throw new ContractViolationException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH,
                "Response missing runtime_digest.");
        }

        return response;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private static string NormalizeOneLine(string stdout)
    {
        string text = stdout.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
        {
            throw new ContractViolationException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Engine stdout must contain exactly one JSON line.");
        }
        return text;
    }
}

/// <summary>Composition root for the v1.5 Engine facade.</summary>
public static class EngineV15Composition
{
    public static EngineFacade Create(EngineV15Options options) => new(options);
}
