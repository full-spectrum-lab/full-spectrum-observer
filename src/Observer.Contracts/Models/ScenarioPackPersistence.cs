namespace FullSpectrum.Observer.Contracts.Models;

public sealed record ScenarioPackRegistration
{
    public required string PackId { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public required string ManifestJson { get; init; }
    public required string InstalledAtUtc { get; init; }
}

public sealed record ScenarioPackTaskBinding
{
    public required string TaskId { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required string PackDigest { get; init; }
    public required string ProfileRefsJson { get; init; }
    public required string BoundAtUtc { get; init; }
}
