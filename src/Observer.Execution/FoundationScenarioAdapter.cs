using System.Text.Json;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Execution;

public sealed class FoundationScenarioAdapter
{
    public AdapterResult Adapt(JsonElement scenario)
    {
        try
        {
            if (scenario.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Scenario must be an object.");
            byte[] canonical = FsObsCanonicalizer.Canonicalize(scenario);
            using JsonDocument document = JsonDocument.Parse(canonical);
            return new AdapterResult(document.RootElement.Clone(), FsObsCanonicalizer.Sha256Hex(canonical));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new AdapterException(FoundationReasonCodes.ADAPTER_MAPPING_FAILED, "Foundation scenario mapping failed.", exception);
        }
    }
}

public sealed class AdapterException : IOException, IReasonCodedException
{
    public AdapterException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
