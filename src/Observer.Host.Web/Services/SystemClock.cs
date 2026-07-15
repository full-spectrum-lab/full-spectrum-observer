namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>Provides the current UTC timestamp (ISO-8601) for audit/storage fields.</summary>
public static class SystemClock
{
    public static string UtcNow => DateTimeOffset.UtcNow.ToString("O");
}
