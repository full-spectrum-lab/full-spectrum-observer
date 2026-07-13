using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.EngineFacade;

public sealed class EngineFacadeException : IOException, IReasonCodedException
{
    public EngineFacadeException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
