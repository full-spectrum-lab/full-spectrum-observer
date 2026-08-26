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
        string rootDirectory,
        string signatureKeyId,
        string trustPurpose)
    {
        Identity = identity;
        Manifest = manifest;
        RootDirectory = rootDirectory;
        SignatureKeyId = signatureKeyId;
        TrustPurpose = trustPurpose;
    }

    public ScenarioPackIdentity Identity { get; }
    public ScenarioPackManifest Manifest { get; }
    public string RootDirectory { get; }
    public string SignatureKeyId { get; }
    public string TrustPurpose { get; }
}
