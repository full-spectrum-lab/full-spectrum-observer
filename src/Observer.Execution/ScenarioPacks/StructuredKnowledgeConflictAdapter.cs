using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed record StructuredKnowledgeConflictResult(
    string SampleId,
    IReadOnlyList<ScenarioPackCandidateObservation> Candidates,
    IReadOnlyList<string> UnknownContext,
    bool MinorityEvidenceSurvived);

/// <summary>
/// Deterministic v0.4 adapter for explicitly structured opposition. It does not infer
/// the meaning of arbitrary natural language. Human-curated assertion keys and polarity
/// remain visible in the transform boundary and every output remains review-required.
/// </summary>
public static class StructuredKnowledgeConflictAdapter
{
    public const string AdapterVersion = "KC-STRUCTURED-OPPOSITION-1";
    public const string CandidateReasonCode = "KC_STRUCTURED_OPPOSITION_CANDIDATE";
    private const int MaximumSources = 128;
    private const int MaximumClaimLength = 4096;

    public static StructuredKnowledgeConflictResult Analyze(
        byte[] inputBytes,
        string resultId,
        string createdAtUtc,
        string signatureKeyId,
        string trustStoreDigest)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureKeyId);
        if (trustStoreDigest.Length != 64 || trustStoreDigest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Trust-store digest must be SHA-256 text.", nameof(trustStoreDigest));
        using JsonDocument document = JsonDocument.Parse(inputBytes);
        JsonElement root = document.RootElement;
        string sampleId = RequireText(root, "sample_id", 128);
        JsonElement sourcesElement = root.GetProperty("sources");
        if (sourcesElement.ValueKind != JsonValueKind.Array ||
            sourcesElement.GetArrayLength() is < 2 or > MaximumSources)
        {
            throw Invalid("sources must contain between 2 and 128 entries.");
        }

        var sources = new List<StructuredSource>(sourcesElement.GetArrayLength());
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement source in sourcesElement.EnumerateArray())
        {
            string sourceId = RequireText(source, "source_id", 128);
            string version = RequireText(source, "version", 128);
            string claim = RequireText(source, "claim", MaximumClaimLength);
            string assertionKey = RequireText(source, "assertion_key", 128);
            if (!IsSafeAssertionKey(assertionKey))
                throw Invalid("assertion_key must contain lowercase ASCII letters, digits, dot, dash or underscore.");
            string polarity = RequireText(source, "polarity", 16);
            if (polarity is not ("AFFIRM" or "DENY" or "UNKNOWN"))
                throw Invalid("polarity must be AFFIRM, DENY or UNKNOWN.");
            string evidenceRole = RequireText(source, "evidence_role", 32);
            if (evidenceRole is not ("PRIMARY" or "MINORITY_CORRECTION" or "CONTEXT"))
                throw Invalid("evidence_role is invalid.");
            string sourceRef = sourceId + "@" + version;
            if (!seenRefs.Add(sourceRef))
                throw Invalid("source_id@version values must be unique.");
            sources.Add(new StructuredSource(
                sourceRef,
                claim,
                assertionKey,
                polarity,
                evidenceRole,
                FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(
                    JsonSerializer.SerializeToElement(new { source_id = sourceId, version, claim })))));
        }

        var candidates = new List<ScenarioPackCandidateObservation>();
        IGrouping<string, StructuredSource>[] groups = sources
            .GroupBy(source => source.AssertionKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        string[] unknown = groups
            .Where(group => !group.Any(source => source.Polarity == "AFFIRM") ||
                            !group.Any(source => source.Polarity == "DENY"))
            .Select(group => $"opposition_not_established:{group.Key}")
            .ToArray();
        foreach (IGrouping<string, StructuredSource> group in groups)
        {
            StructuredSource[] affirm = group.Where(source => source.Polarity == "AFFIRM").ToArray();
            StructuredSource[] deny = group.Where(source => source.Polarity == "DENY").ToArray();
            StructuredSource[] unresolved = group.Where(source => source.Polarity == "UNKNOWN").ToArray();
            if (affirm.Length == 0 || deny.Length == 0)
                continue;

            StructuredSource[] involved = affirm.Concat(deny)
                .OrderBy(source => source.SourceRef, StringComparer.Ordinal)
                .ToArray();
            string[] sourceRefs = involved.Select(source => source.SourceRef).ToArray();
            string[] claimDigests = involved.Select(source => source.ClaimDigest).ToArray();
            string[] minorityRefs = involved
                .Where(source => source.EvidenceRole == "MINORITY_CORRECTION")
                .Select(source => source.SourceRef)
                .ToArray();
            string[] missing = unresolved.Select(source => "polarity_unknown:" + source.SourceRef)
                .Concat(minorityRefs.Length == 0 ? ["minority_evidence_not_declared"] : [])
                .Concat(unknown.Where(context => !context.EndsWith(":" + group.Key, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            byte[] material = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                adapter_version = AdapterVersion,
                sample_id = sampleId,
                assertion_key = group.Key,
                source_refs = sourceRefs,
                claim_digests = claimDigests,
                minority_evidence_refs = minorityRefs,
                missing_context = missing,
                reason_code = CandidateReasonCode,
                signature_key_id = signatureKeyId,
                trust_store_digest = trustStoreDigest,
                human_review_required = true,
                authorized_action = false,
            }));
            string digest = FsObsCanonicalizer.Sha256Hex(material);
            candidates.Add(new ScenarioPackCandidateObservation
            {
                CandidateId = "CAND-V04-" + digest[..20],
                ResultId = resultId,
                SampleId = sampleId,
                AssertionKey = group.Key,
                SourceRefs = sourceRefs.ToImmutableArray(),
                ClaimDigests = claimDigests.ToImmutableArray(),
                MinorityEvidenceRefs = minorityRefs.ToImmutableArray(),
                MissingContext = missing.ToImmutableArray(),
                ReasonCode = CandidateReasonCode,
                AdapterVersion = AdapterVersion,
                SignatureKeyId = signatureKeyId,
                TrustStoreDigest = trustStoreDigest.ToLowerInvariant(),
                HumanReviewRequired = true,
                AuthorizedAction = false,
                CandidateDigest = digest,
                CreatedAtUtc = createdAtUtc,
            });
        }

        return new StructuredKnowledgeConflictResult(
            sampleId,
            candidates,
            unknown,
            candidates.SelectMany(candidate => candidate.MinorityEvidenceRefs).Any());
    }

    private static string RequireText(JsonElement element, string name, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} is required and must be a string.");
        }
        string value = property.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw Invalid($"{name} is blank or exceeds its length limit.");
        return value;
    }

    private static bool IsSafeAssertionKey(string value)
    {
        if (value.Length == 0 || value[0] is '.' or '-' or '_') return false;
        return value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
    }

    private static InvalidDataException Invalid(string message) =>
        new("SCENARIO_PACK_STRUCTURED_INPUT_INVALID: " + message);

    private sealed record StructuredSource(
        string SourceRef,
        string Claim,
        string AssertionKey,
        string Polarity,
        string EvidenceRole,
        string ClaimDigest);
}
