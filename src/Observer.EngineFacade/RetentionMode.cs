namespace FullSpectrum.Observer.EngineFacade;

/// <summary>
/// Raw Analysis Input retention modes (R1-B §6). The Engine performs analysis-time
/// desensitization; the retention mode decides what the Observer persists locally.
/// Connector write-back is always OFF (red line #3) — none of these modes write back to
/// any external business system.
/// </summary>
public enum RetentionMode
{
    /// <summary>Default. Persist canonical (desensitized) input + trace + evidence digest + references.</summary>
    SanitizedPersistent,

    /// <summary>Sanitized persistent plus the local original (local file only, never leaves the machine).</summary>
    FullLocal,

    /// <summary>One-shot analysis; persist only digests + references, never the input.</summary>
    Ephemeral,
}

/// <summary>Wire-string mapping for <see cref="RetentionMode"/>.</summary>
public static class RetentionModeExtensions
{
    public static string ToWire(this RetentionMode mode) => mode switch
    {
        RetentionMode.SanitizedPersistent => "SANITIZED_PERSISTENT",
        RetentionMode.FullLocal => "FULL_LOCAL",
        RetentionMode.Ephemeral => "EPHEMERAL",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown retention mode."),
    };

    public static RetentionMode FromWire(string value) => value switch
    {
        "SANITIZED_PERSISTENT" => RetentionMode.SanitizedPersistent,
        "FULL_LOCAL" => RetentionMode.FullLocal,
        "EPHEMERAL" => RetentionMode.Ephemeral,
        _ => throw new ArgumentException($"Unknown retention mode wire value: {value}", nameof(value)),
    };
}
