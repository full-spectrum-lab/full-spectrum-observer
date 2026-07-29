using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.EngineFacade;

/// <summary>Input to <see cref="IntakeAdapter.BuildEnvelope"/>.</summary>
public sealed record BuildEnvelopeRequest(
    string CaseId,
    ObservedSubject Subject,
    SubjectVersion SubjectVersion,
    IReadOnlyList<KnowledgeSourceVersion> KnowledgeVersions,
    RawAnalysisInput Input,
    RetentionMode RetentionMode);

/// <summary>
/// Builds the Engine v1.5 request envelope from Console input. Performs STRUCTURE validation
/// and version binding ONLY (Schema/Governance separation, shared-knowledge #2). It never makes
/// any governance judgement about the subject, knowledge, or conclusion.
/// </summary>
public sealed class IntakeAdapter
{
    /// <summary>Assembles the normalized request envelope (all three intake modes already normalized).</summary>
    public EngineRequest BuildEnvelope(BuildEnvelopeRequest request)
    {
        var subject = new EngineSubject
        {
            LocalSubjectId = request.Subject.LocalSubjectId,
            SubjectType = request.Subject.SubjectType,
            Mode = request.Subject.Mode,
            ConcentrationTier = request.Subject.ConcentrationTier,
            Declaration = JsonSerializer.Deserialize<JsonElement>(request.SubjectVersion.Payload),
        };

        var knowledge = request.KnowledgeVersions.Select(static k => new EngineKnowledge
        {
            SourceId = k.SourceId,
            VersionId = k.VersionId,
            Digest = k.Digest,
            Applicability = k.Applicability,
        }).ToList();

        var input = new EngineInput
        {
            Mode = request.Input.Mode,
            CanonicalInput = JsonSerializer.Deserialize<JsonElement>(request.Input.CanonicalInput),
            ContentDigest = request.Input.ContentDigest,
            TransformTrace = JsonSerializer.Deserialize<JsonElement>(request.Input.TransformTrace ?? "[]"),
        };

        return new EngineRequest
        {
            EnvelopeVersion = EngineV15Contract.EnvelopeVersion,
            AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
            EngineVersion = EngineV15Contract.EngineTag,
            EngineCommit = EngineV15Contract.EngineCommit,
            ProfileVersion = EngineV15Contract.ProfileVersion,
            SchemaVersion = EngineV15Contract.SchemaVersion,
            SchemaDigest = EngineV15Contract.SchemaDigest,
            CaseId = request.CaseId,
            Subject = subject,
            Knowledge = knowledge,
            Input = input,
            RetentionMode = request.RetentionMode.ToWire(),
        };
    }

    /// <summary>
    /// Validates the envelope structure (and version binding). Throws
    /// <see cref="IntakeValidationException"/> on structural problems. Deliberately does NOT
    /// validate governance conclusions — that is the Engine's responsibility.
    /// </summary>
    public void ValidateSchema(EngineRequest request)
    {
        if (request is null)
        {
            throw new IntakeValidationException(FoundationReasonCodes.SCHEMA_REQUIRED_MISSING, "Engine request envelope is null.");
        }
        if (!string.Equals(request.EngineVersion, EngineV15Contract.EngineTag, StringComparison.Ordinal))
        {
            throw new IntakeValidationException(FoundationReasonCodes.ENGINE_VERSION_MISMATCH, "Envelope engine_version is not pinned to 1.5.0.");
        }
        if (string.IsNullOrWhiteSpace(request.Subject.LocalSubjectId))
        {
            throw new IntakeValidationException(FoundationReasonCodes.SCHEMA_REQUIRED_MISSING, "Envelope subject.local_subject_id is required.");
        }
        if (request.Knowledge is null)
        {
            throw new IntakeValidationException(FoundationReasonCodes.SCHEMA_REQUIRED_MISSING, "Envelope knowledge[] is required.");
        }
        if (request.Input is null || string.IsNullOrWhiteSpace(request.Input.ContentDigest))
        {
            throw new IntakeValidationException(FoundationReasonCodes.SCHEMA_REQUIRED_MISSING, "Envelope input.content_digest is required.");
        }
    }
}
