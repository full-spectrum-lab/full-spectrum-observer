using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Evidence;

public sealed class EvidenceStoreException : IOException, IReasonCodedException
{
    public EvidenceStoreException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
