namespace FullSpectrum.Observer.EngineFacade;

public sealed record EngineFacadeOptions
{
    public required string PythonExecutablePath { get; init; }
    public required string WorkerScriptPath { get; init; }
    public required string EngineRootPath { get; init; }
    public required string WorkerLockPath { get; init; }
    public required string SchemaDirectory { get; init; }
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan KillGracePeriod { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumResponseBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumStandardErrorBytes { get; init; } = 1024 * 1024;

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(PythonExecutablePath) || !File.Exists(PythonExecutablePath))
            throw new FileNotFoundException("Private Python executable is missing or is not an absolute path.", PythonExecutablePath);
        if (!Path.IsPathFullyQualified(WorkerScriptPath) || !File.Exists(WorkerScriptPath))
            throw new FileNotFoundException("Engine Worker script is missing or is not an absolute path.", WorkerScriptPath);
        if (!Path.IsPathFullyQualified(EngineRootPath) || !Directory.Exists(EngineRootPath))
            throw new DirectoryNotFoundException("Pinned Engine root is missing or is not an absolute path.");
        if (!Path.IsPathFullyQualified(WorkerLockPath) || !File.Exists(WorkerLockPath))
            throw new FileNotFoundException("Worker integrity lock is missing.", WorkerLockPath);
        if (!Path.IsPathFullyQualified(SchemaDirectory) || !Directory.Exists(SchemaDirectory))
            throw new DirectoryNotFoundException("Foundation Schema directory is missing or is not an absolute path.");
        if (DefaultTimeout < TimeSpan.FromSeconds(1) || DefaultTimeout > TimeSpan.FromSeconds(300))
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout));
        if (MaximumResponseBytes <= 0 || MaximumStandardErrorBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}
