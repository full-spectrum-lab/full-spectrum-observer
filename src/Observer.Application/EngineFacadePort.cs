using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Application;

public sealed record EngineFacadeExecutionResult(
    EngineFacadeResponse Response,
    ReadOnlyMemory<byte> RawResponseBytes,
    int ExitCode,
    long DurationMilliseconds,
    string StandardErrorSha256,
    long StandardErrorBytes);

public interface IObserverEngineFacade
{
    Task<EngineFacadeExecutionResult> EvaluateAsync(EngineFacadeRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}
