namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed class ScenarioPackRegistry
{
    private readonly Dictionary<(string PackId, string Version), LoadedScenarioPack> _installed = new();
    private readonly object _gate = new();

    public ScenarioPackIdentity Install(LoadedScenarioPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var key = (pack.Identity.PackId, pack.Identity.Version);
        lock (_gate)
        {
            if (_installed.TryGetValue(key, out LoadedScenarioPack? existing))
            {
                if (!string.Equals(existing.Identity.Digest, pack.Identity.Digest, StringComparison.Ordinal))
                {
                    throw new ScenarioPackLoadException(
                        ScenarioPackReasonCodes.IdentityConflict,
                        $"{pack.Identity.PackId}@{pack.Identity.Version} is already frozen with a different digest.");
                }
                return existing.Identity;
            }
            _installed.Add(key, pack);
            return pack.Identity;
        }
    }

    public LoadedScenarioPack Resolve(string packId, string version)
    {
        lock (_gate)
        {
            if (_installed.TryGetValue((packId, version), out LoadedScenarioPack? pack))
            {
                return pack;
            }
        }
        throw new ScenarioPackLoadException(ScenarioPackReasonCodes.IdentityNotFound, $"Pack is not installed: {packId}@{version}.");
    }
}
