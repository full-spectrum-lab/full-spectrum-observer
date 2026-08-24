namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed class ScenarioPackLoadException : Exception
{
    public ScenarioPackLoadException(string reasonCode, string message, Exception? innerException = null)
        : base($"{reasonCode}: {message}", innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
