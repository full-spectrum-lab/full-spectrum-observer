namespace FullSpectrum.Observer.EngineFacade;

/// <summary>Runtime options for invoking the pinned Engine v1.5.0 (single local operator identity).</summary>
public sealed record EngineV15Options
{
    /// <summary>Absolute path to the private Python executable.</summary>
    public required string PythonExecutablePath { get; init; }

    /// <summary>Absolute path to the pinned Engine v1.5.0 root.</summary>
    public required string EngineRootPath { get; init; }

    /// <summary>Python module invoked, e.g. <c>governance_chain</c>.</summary>
    public string EngineModule { get; init; } = "governance_chain";

    /// <summary>Default analysis timeout.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public void Validate()
    {
        // NOTE: Empty PythonExecutablePath / EngineRootPath is a legitimate "Engine not
        // configured" state — the real dependency check happens at analysis time
        // (EngineFacade.AnalyzeAsync throws DependencyMissingException -> "依赖缺失/不可重放").
        // We intentionally do NOT throw here so the console can start and be used for
        // subject/knowledge/audit management even when the Engine is absent.
        if (DefaultTimeout < TimeSpan.FromSeconds(1) || DefaultTimeout > TimeSpan.FromSeconds(600))
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout));
    }
}
