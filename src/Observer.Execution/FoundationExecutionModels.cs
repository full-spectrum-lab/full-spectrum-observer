using System.Text.Json;

namespace FullSpectrum.Observer.Execution;

public sealed record IntakeResult(
    ReadOnlyMemory<byte> RawBytes,
    JsonElement Scenario,
    string InputSha256,
    string SourceKind);

public sealed record AdapterResult(
    JsonElement CanonicalContext,
    string CanonicalContextSha256);

public sealed record GovernanceValidationSummary(
    bool IsSufficient,
    IReadOnlyList<string> UnknownFields,
    IReadOnlyList<string> ReasonCodes);
