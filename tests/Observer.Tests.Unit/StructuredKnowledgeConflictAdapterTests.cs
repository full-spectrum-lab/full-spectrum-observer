using System.Text;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class StructuredKnowledgeConflictAdapterTests
{
    private const string TrustDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Explicit_opposition_preserves_minority_source_without_persisting_claim_text()
    {
        byte[] input = Encoding.UTF8.GetBytes("""
            {
              "sample_id":"RP-001",
              "sources":[
                {
                  "source_id":"ai-first","version":"1","claim":"The pilot log is the owner's real evolution.",
                  "assertion_key":"pilot_log_is_owner_real_evolution","polarity":"AFFIRM","evidence_role":"PRIMARY"
                },
                {
                  "source_id":"owner-correction","version":"1","claim":"The pilot log is not my real document evolution.",
                  "assertion_key":"pilot_log_is_owner_real_evolution","polarity":"DENY","evidence_role":"MINORITY_CORRECTION"
                }
              ]
            }
            """);

        StructuredKnowledgeConflictResult result = StructuredKnowledgeConflictAdapter.Analyze(
            input, "RESULT-1", "2026-08-27T00:00:00Z", "owner-rp001", TrustDigest);

        result.Candidates.Should().ContainSingle();
        result.MinorityEvidenceSurvived.Should().BeTrue();
        result.UnknownContext.Should().BeEmpty();
        ScenarioPackCandidateObservation candidate = result.Candidates.Single();
        candidate.ResultId.Should().Be("RESULT-1");
        candidate.ReasonCode.Should().Be("KC_STRUCTURED_OPPOSITION_CANDIDATE");
        candidate.HumanReviewRequired.Should().BeTrue();
        candidate.AuthorizedAction.Should().BeFalse();
        candidate.SignatureKeyId.Should().Be("owner-rp001");
        candidate.TrustStoreDigest.Should().Be(TrustDigest);
        candidate.SourceRefs.Should().Equal("ai-first@1", "owner-correction@1");
        candidate.MinorityEvidenceRefs.Should().Equal("owner-correction@1");
        candidate.ClaimDigests.Should().OnlyContain(digest => digest.Length == 64);
    }

    [Fact]
    public void Unsupported_natural_language_without_structured_polarity_fails_closed()
    {
        byte[] input = Encoding.UTF8.GetBytes("""
            {"sample_id":"RP-002","sources":[
              {"source_id":"a","version":"1","claim":"one"},
              {"source_id":"b","version":"1","claim":"two"}
            ]}
            """);

        Action analyze = () => StructuredKnowledgeConflictAdapter.Analyze(
            input, "RESULT-2", "2026-08-27T00:00:00Z", "owner-rp002", TrustDigest);

        analyze.Should().Throw<InvalidDataException>()
            .WithMessage("SCENARIO_PACK_STRUCTURED_INPUT_INVALID*");
    }

    [Fact]
    public void Same_polarity_does_not_invent_a_conflict_and_preserves_unknown()
    {
        byte[] input = Encoding.UTF8.GetBytes("""
            {"sample_id":"RP-003","sources":[
              {"source_id":"a","version":"1","claim":"one","assertion_key":"same","polarity":"AFFIRM","evidence_role":"PRIMARY"},
              {"source_id":"b","version":"1","claim":"two","assertion_key":"same","polarity":"AFFIRM","evidence_role":"CONTEXT"}
            ]}
            """);

        StructuredKnowledgeConflictResult result = StructuredKnowledgeConflictAdapter.Analyze(
            input, "RESULT-3", "2026-08-27T00:00:00Z", "owner-rp003", TrustDigest);

        result.Candidates.Should().BeEmpty();
        result.MinorityEvidenceSurvived.Should().BeFalse();
        result.UnknownContext.Should().Equal("opposition_not_established:same");
    }

    [Fact]
    public void Sample_unknown_context_survives_on_candidate_boundary()
    {
        byte[] input = Encoding.UTF8.GetBytes("""
            {"sample_id":"RP-004","sources":[
              {"source_id":"a","version":"1","claim":"one","assertion_key":"same","polarity":"AFFIRM","evidence_role":"PRIMARY"},
              {"source_id":"b","version":"1","claim":"not one","assertion_key":"same","polarity":"DENY","evidence_role":"MINORITY_CORRECTION"},
              {"source_id":"scan","version":"UNKNOWN","claim":"scan unavailable","assertion_key":"scan_manifest_available","polarity":"UNKNOWN","evidence_role":"CONTEXT"}
            ]}
            """);

        StructuredKnowledgeConflictResult result = StructuredKnowledgeConflictAdapter.Analyze(
            input, "RESULT-4", "2026-08-27T00:00:00Z", "owner-rp004", TrustDigest);

        result.Candidates.Should().ContainSingle();
        result.UnknownContext.Should().Equal("opposition_not_established:scan_manifest_available");
        result.Candidates.Single().MissingContext.Should().Contain("opposition_not_established:scan_manifest_available");
    }
}
