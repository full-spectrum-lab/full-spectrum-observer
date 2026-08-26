using System.Collections.Immutable;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Observer-owned, sample-bounded candidate produced by a declared Scenario Pack adapter.
/// It is deliberately separate from <see cref="ConflictObservation"/>, which remains an
/// Engine-owned pass-through contract.
/// </summary>
public sealed record ScenarioPackCandidateObservation
{
    public required string CandidateId { get; init; }
    public required string ResultId { get; init; }
    public required string SampleId { get; init; }
    public required string AssertionKey { get; init; }
    public required ImmutableArray<string> SourceRefs { get; init; }
    public required ImmutableArray<string> ClaimDigests { get; init; }
    public required ImmutableArray<string> MinorityEvidenceRefs { get; init; }
    public required ImmutableArray<string> MissingContext { get; init; }
    public required string ReasonCode { get; init; }
    public required string AdapterVersion { get; init; }
    public required string SignatureKeyId { get; init; }
    public required string TrustStoreDigest { get; init; }
    public required bool HumanReviewRequired { get; init; }
    public required bool AuthorizedAction { get; init; }
    public required string CandidateDigest { get; init; }
    public required string CreatedAtUtc { get; init; }
}
