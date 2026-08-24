using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed record ScenarioPackIdentity(string PackId, string Version, string Digest)
{
    public override string ToString() => $"{PackId}@{Version}@{Digest}";
}

public sealed class LoadedScenarioPack
{
    internal LoadedScenarioPack(
        ScenarioPackIdentity identity,
        ScenarioPackManifest manifest,
        string rootDirectory)
    {
        Identity = identity;
        Manifest = manifest;
        RootDirectory = rootDirectory;
    }

    public ScenarioPackIdentity Identity { get; }
    public ScenarioPackManifest Manifest { get; }
    public string RootDirectory { get; }
}
