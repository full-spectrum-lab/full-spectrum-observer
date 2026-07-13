using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Allows read-only health/show/audit commands to compose when the private
/// Python/Engine runtime is not installed. Analyze returns a structured error.
/// </summary>
public sealed class UnavailableEngineFacade : IObserverEngineFacade
{
    public Task<EngineFacadeExecutionResult> EvaluateAsync(
        EngineFacadeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EngineFacadeResponse response = new()
        {
            Protocol = "fs-observer-engine-facade/1",
            RequestId = request.RequestId,
            Status = "ERROR",
            EngineVersion = BuildIdentity.EngineVersion,
            EngineCommit = BuildIdentity.EngineCommit,
            OutputSerialization = null,
            OutputSha256 = null,
            Output = null,
            Error = JsonSerializer.SerializeToElement(new
            {
                code = FoundationReasonCodes.SYSTEM_DEPENDENCY_MISSING,
                message = "Private Python or pinned Engine runtime is not configured.",
                details_redacted = true,
            }, FoundationJson.CreateOptions()),
        };
        return Task.FromResult(new EngineFacadeExecutionResult(
            response,
            FoundationJson.Serialize(response),
            70,
            0,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())),
            0));
    }
}
