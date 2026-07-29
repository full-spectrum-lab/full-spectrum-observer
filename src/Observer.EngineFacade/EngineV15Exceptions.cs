using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>Raised when the Engine response violates the frozen v1.5.0 contract.
/// The caller must write a WARNING audit and block persistence (R1-B §5.2).</summary>
public sealed class ContractViolationException : Exception, IReasonCodedException
{
    public ContractViolationException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>Raised when the Engine version binding is wrong (not 1.5.0, or missing commit).</summary>
public sealed class VersionBindingException : Exception, IReasonCodedException
{
    public VersionBindingException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>Raised when the pinned Engine v1.5.0 dependency is missing — surfaces as
/// "依赖缺失/不可重放" (dependency missing / not replayable), blocking the task (red line #8 / TC-NEW-003).</summary>
public sealed class DependencyMissingException : Exception, IReasonCodedException
{
    public DependencyMissingException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>Raised by <see cref="IntakeAdapter"/> when the request envelope fails structure validation.</summary>
public sealed class IntakeValidationException : Exception, IReasonCodedException
{
    public IntakeValidationException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
