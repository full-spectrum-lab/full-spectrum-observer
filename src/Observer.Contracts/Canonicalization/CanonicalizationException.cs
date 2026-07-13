using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Contracts.Canonicalization;

public sealed class CanonicalizationException : IOException
{
    public CanonicalizationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public string ReasonCode => FoundationReasonCodes.AUDIT_CANONICALIZATION_FAILED;
}
