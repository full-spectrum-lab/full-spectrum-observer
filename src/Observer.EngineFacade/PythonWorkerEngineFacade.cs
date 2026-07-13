using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.EngineFacade;

public sealed class PythonWorkerEngineFacade : IObserverEngineFacade
{
    private readonly EngineFacadeOptions _options;

    public PythonWorkerEngineFacade(EngineFacadeOptions options)
    {
        options.Validate();
        _options = options;
    }

    public async Task<EngineFacadeExecutionResult> EvaluateAsync(EngineFacadeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(300))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ValidateRequest(request);
        WorkerLockManifest manifest = WorkerIntegrityVerifier.Verify(_options.WorkerLockPath);
        if (!string.Equals(manifest.EngineVersion, BuildIdentity.EngineVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.EngineCommit, BuildIdentity.EngineCommit, StringComparison.Ordinal))
        {
            throw new EngineFacadeException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH, "Worker lock Engine identity does not match the frozen baseline.");
        }

        byte[] requestBytes = FoundationJson.Serialize(request);
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
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                throw new EngineFacadeException(FoundationReasonCodes.FACADE_WORKER_NOT_FOUND, "Python Worker process did not start.");
        }
        catch (EngineFacadeException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_WORKER_NOT_FOUND, "Python Worker process start failed.", exception);
        }

        Task<byte[]> stdoutTask = LimitedStreamReader.ReadAsync(process.StandardOutput.BaseStream, _options.MaximumResponseBytes, FoundationReasonCodes.FACADE_RESPONSE_TOO_LARGE);
        Task<byte[]> stderrTask = LimitedStreamReader.ReadAsync(process.StandardError.BaseStream, _options.MaximumStandardErrorBytes, FoundationReasonCodes.FACADE_RESPONSE_TOO_LARGE);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, CancellationToken.None).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stdoutTask).ConfigureAwait(false);
            await ObserveStreamTaskAsync(stderrTask).ConfigureAwait(false);
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Failed to write the request to the Python Worker.", exception);
        }

        bool callerCancelled = false;
        bool timedOut = false;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            callerCancelled = cancellationToken.IsCancellationRequested;
            timedOut = !callerCancelled && timeoutSource.IsCancellationRequested;
            await TerminateAsync(process).ConfigureAwait(false);
        }

        byte[] stdout = await stdoutTask.ConfigureAwait(false);
        byte[] stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();
        string stderrDigest = Convert.ToHexStringLower(SHA256.HashData(stderr));

        if (callerCancelled || timedOut)
        {
            string status = callerCancelled ? "CANCELLED" : "TIMED_OUT";
            string reason = callerCancelled ? FoundationReasonCodes.ENGINE_CANCELLED : FoundationReasonCodes.ENGINE_TIMEOUT;
            EngineFacadeResponse synthetic = ErrorResponse(request.RequestId, status, reason, callerCancelled ? "Engine execution was cancelled." : "Engine execution timed out.");
            return new EngineFacadeExecutionResult(synthetic, FoundationJson.Serialize(synthetic), process.HasExited ? process.ExitCode : 60, stopwatch.ElapsedMilliseconds, stderrDigest, stderr.LongLength);
        }

        byte[] oneLine = NormalizeOneLine(stdout);
        EngineFacadeResponse response;
        try
        {
            using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(_options.SchemaDirectory);
            SchemaValidationReport validation = bundle.Validate("engine-facade-response", oneLine);
            if (!validation.IsValid)
                throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Worker response failed the frozen EngineFacadeResponse Schema.");
            response = FoundationJson.Deserialize<EngineFacadeResponse>(oneLine);
        }
        catch (EngineFacadeException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Worker response is not valid protocol JSON.", exception);
        }

        ValidateResponse(request, response);
        return new EngineFacadeExecutionResult(response, oneLine, process.ExitCode, stopwatch.ElapsedMilliseconds, stderrDigest, stderr.LongLength);
    }

    private void ValidateRequest(EngineFacadeRequest request)
    {
        byte[] requestBytes = FoundationJson.Serialize(request);
        using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(_options.SchemaDirectory);
        SchemaValidationReport report = bundle.Validate("engine-facade-request", requestBytes);
        if (!report.IsValid)
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "EngineFacadeRequest failed the frozen Schema.");
    }

    private static void ValidateResponse(EngineFacadeRequest request, EngineFacadeResponse response)
    {
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(response.EngineVersion, BuildIdentity.EngineVersion, StringComparison.Ordinal) ||
            !string.Equals(response.EngineCommit, BuildIdentity.EngineCommit, StringComparison.Ordinal))
            throw new EngineFacadeException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH, "Worker response identity does not match the request and frozen Engine.");

        if (string.Equals(response.Status, "SUCCESS", StringComparison.Ordinal))
        {
            if (response.Output is null || string.IsNullOrWhiteSpace(response.OutputSha256) ||
                !string.Equals(response.OutputSerialization, "FSE-PYJSON-1", StringComparison.Ordinal))
                throw new EngineFacadeException(FoundationReasonCodes.ENGINE_OUTPUT_INVALID, "Successful Worker response is missing output fields.");
            byte[] rawOutput = Encoding.UTF8.GetBytes(response.Output.Value.GetRawText());
            string digest = Convert.ToHexStringLower(SHA256.HashData(rawOutput));
            if (!string.Equals(digest, response.OutputSha256, StringComparison.Ordinal))
                throw new EngineFacadeException(FoundationReasonCodes.OUTPUT_DIGEST_MISMATCH, "Worker output SHA-256 does not match the embedded raw output.");
        }
    }

    private static async Task ObserveStreamTaskAsync(Task<byte[]> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (EngineFacadeException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private async Task TerminateAsync(Process process)
    {
        if (process.HasExited) return;
        using var grace = new CancellationTokenSource(_options.KillGracePeriod);
        try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); return; }
        catch (OperationCanceledException) { }
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static byte[] NormalizeOneLine(byte[] stdout)
    {
        string text = Encoding.UTF8.GetString(stdout).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Worker stdout must contain exactly one JSON line.");
        return Encoding.UTF8.GetBytes(text);
    }

    private static EngineFacadeResponse ErrorResponse(string requestId, string status, string code, string message) => new()
    {
        Protocol = "fs-observer-engine-facade/1",
        RequestId = requestId,
        Status = status,
        EngineVersion = BuildIdentity.EngineVersion,
        EngineCommit = BuildIdentity.EngineCommit,
        OutputSerialization = null,
        OutputSha256 = null,
        Output = null,
        Error = JsonSerializer.SerializeToElement(new { code, message, details_redacted = true }),
    };
}
