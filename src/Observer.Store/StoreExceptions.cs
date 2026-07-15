namespace FullSpectrum.Observer.Store;

/// <summary>Base exception for local store failures.</summary>
public class StoreException : Exception
{
    public string ReasonCode { get; }

    public StoreException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }
}

/// <summary>
/// Raised when an operation would mutate an immutable (Active) versioned row, or otherwise
/// violate the ADR-001 immutability discipline (red line #7 / R+1).
/// </summary>
public sealed class ImmutableVersionException : StoreException
{
    public ImmutableVersionException(string message)
        : base("STORE_VERSION_IMMUTABLE", message)
    {
    }
}

/// <summary>
/// Raised when the append-only audit chain is found to be broken (red line #7 / ADR-002).
/// </summary>
public sealed class AuditChainBrokenException : StoreException
{
    public string? BrokenAtAuditId { get; }

    public AuditChainBrokenException(string? brokenAtAuditId, string message)
        : base("AUDIT_CHAIN_BROKEN", message)
    {
        BrokenAtAuditId = brokenAtAuditId;
    }
}
