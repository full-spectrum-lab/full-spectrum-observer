using System.Threading;
using System.Threading.Tasks;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// Abstraction over the pinned Engine v1.5.0 facade. The AnalysisWorkspace orchestrator
/// depends on this interface so it can be driven by the real EngineFacade (process invocation of
/// the frozen Engine worker) or by a test double (a fake Engine returning a canned,
/// contract-valid response) without changing the Engine contract or its frozen commit.
/// </summary>
public interface IEngineFacade
{
    /// <summary>
    /// Sends the request envelope to the Engine and returns the validated v1.5 response envelope.
    /// Throws on any version-binding / dependency / contract deviation (never silently downgrades).
    /// </summary>
    Task<EngineResponse> AnalyzeAsync(EngineRequest request, CancellationToken cancellationToken = default);
}
