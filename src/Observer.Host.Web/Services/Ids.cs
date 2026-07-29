namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>Generates stable, collision-resistant local identifiers.</summary>
public static class Ids
{
    public static string Next(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
