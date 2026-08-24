using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.EngineFacade;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed record ScenarioPackEngineRequest(
    ScenarioPackIdentity PackIdentity,
    string CaseId,
    IReadOnlyList<ScenarioPackProfileIdentity> Profiles,
    string InputDigest,
    EngineRequest Request);

public sealed record ScenarioPackProfileIdentity(string Reference, string ProfileId, string Version, string Digest);

public sealed class ScenarioPackEngineRequestFactory
{
    public ScenarioPackEngineRequest BuildGoldenRequest(LoadedScenarioPack pack, string caseId)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ScenarioPackGoldenCase goldenCase = pack.Manifest.GoldenCases.SingleOrDefault(
            item => string.Equals(item.CaseId, caseId, StringComparison.Ordinal))
            ?? throw new ScenarioPackLoadException(
                ScenarioPackReasonCodes.ReferenceMissing,
                $"Golden case is not declared by the Pack: {caseId}.");

        string inputPath = ResolveContainedFile(pack.RootDirectory, goldenCase.InputRef);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        return BuildRequest(pack, goldenCase.CaseId, inputBytes, "SCENARIO_PACK_GOLDEN");
    }

    public ScenarioPackEngineRequest BuildRequest(
        LoadedScenarioPack pack,
        string caseId,
        byte[] inputBytes,
        string executionMode = "SCENARIO_PACK_BATCH")
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(inputBytes);
        ValidateInput(pack, inputBytes);
        using JsonDocument inputDocument = JsonDocument.Parse(inputBytes);
        IReadOnlyList<ScenarioPackProfileIdentity> profiles = LoadProfiles(pack);

        string simulationId = "SP-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{pack.Identity.PackId}\n{pack.Identity.Version}\n{pack.Identity.Digest}\n{caseId}")))[..24];
        JsonElement scenario = JsonSerializer.SerializeToElement(new
        {
            simulation_id = simulationId,
            scenario_pack = new
            {
                pack_id = pack.Identity.PackId,
                version = pack.Identity.Version,
                digest = pack.Identity.Digest,
            },
            case_id = caseId,
            policy_profiles = profiles.Select(profile => new
            {
                reference = profile.Reference,
                profile_id = profile.ProfileId,
                version = profile.Version,
                digest = profile.Digest,
            }),
            pack_input = inputDocument.RootElement,
            input_query = pack.Manifest.ExpectedObservation,
            initial_state = new { survival = 0.5m, coordination = 0.5m, meaning = 0.5m },
            sensitivity_level = "synthetic",
            enterprise_id = "observer-v04-local",
            rule_version = pack.Identity.Version,
        });
        byte[] canonicalScenario = FsObsCanonicalizer.Canonicalize(scenario);
        string inputDigest = FsObsCanonicalizer.Sha256Hex(canonicalScenario);
        using JsonDocument canonicalScenarioDocument = JsonDocument.Parse(canonicalScenario);
        JsonElement canonicalScenarioElement = canonicalScenarioDocument.RootElement.Clone();

        var request = new EngineRequest
        {
            EnvelopeVersion = EngineV15Contract.EnvelopeVersion,
            AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
            EngineVersion = EngineV15Contract.EngineTag,
            EngineCommit = EngineV15Contract.EngineCommit,
            ProfileVersion = EngineV15Contract.ProfileVersion,
            SchemaVersion = EngineV15Contract.SchemaVersion,
            SchemaDigest = EngineV15Contract.SchemaDigest,
            CaseId = caseId,
            Subject = new EngineSubject
            {
                LocalSubjectId = $"PACK:{pack.Identity.PackId}",
                SubjectType = pack.Manifest.SubjectTypes[0],
                Mode = executionMode,
                Declaration = JsonSerializer.SerializeToElement(new
                {
                    pack_id = pack.Identity.PackId,
                    pack_version = pack.Identity.Version,
                    pack_digest = pack.Identity.Digest,
                    candidate_only = true,
                    human_review_required = true,
                }),
            },
            Knowledge = [],
            Input = new EngineInput
            {
                Mode = "JSON_IMPORT",
                CanonicalInput = canonicalScenarioElement,
                ContentDigest = inputDigest,
                TransformTrace = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        operation = "SCENARIO_PACK_ENVELOPE",
                        source_digest = FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(inputDocument.RootElement)),
                        pack_digest = pack.Identity.Digest,
                        profile_digests = profiles.Select(profile => profile.Digest).ToArray(),
                    },
                }),
            },
            RetentionMode = "SANITIZED_PERSISTENT",
        };
        return new ScenarioPackEngineRequest(pack.Identity, caseId, profiles, inputDigest, request);
    }

    public static string CreateProfileBindingJson(ScenarioPackEngineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Encoding.UTF8.GetString(FsObsCanonicalizer.Canonicalize(
            JsonSerializer.SerializeToElement(request.Profiles.Select(profile => new
            {
                reference = profile.Reference,
                profile_id = profile.ProfileId,
                version = profile.Version,
                digest = profile.Digest,
            }))));
    }

    private static IReadOnlyList<ScenarioPackProfileIdentity> LoadProfiles(LoadedScenarioPack pack)
    {
        var profiles = new List<ScenarioPackProfileIdentity>(pack.Manifest.PolicyProfileRefs.Length);
        foreach (string reference in pack.Manifest.PolicyProfileRefs)
        {
            string path = ResolveContainedFile(pack.RootDirectory, reference);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            string[] names = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
                : [];
            string[] expected =
            [
                "candidate_only", "human_review_required", "preserve_unknown", "profile_id", "version",
            ];
            if (!names.SequenceEqual(expected, StringComparer.Ordinal) ||
                !root.GetProperty("candidate_only").GetBoolean() ||
                !root.GetProperty("preserve_unknown").GetBoolean() ||
                !root.GetProperty("human_review_required").GetBoolean())
            {
                throw new ScenarioPackLoadException(
                    ScenarioPackReasonCodes.ManifestInvalid,
                    $"Pack Profile must use the strict candidate-only v0.4 shape: {reference}.");
            }
            string profileId = root.GetProperty("profile_id").GetString() ?? string.Empty;
            string version = root.GetProperty("version").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(version))
            {
                throw new ScenarioPackLoadException(
                    ScenarioPackReasonCodes.ManifestInvalid,
                    $"Pack Profile identity is incomplete: {reference}.");
            }
            profiles.Add(new ScenarioPackProfileIdentity(
                reference,
                profileId,
                version,
                FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(root))));
        }
        return profiles;
    }

    private static void ValidateInput(LoadedScenarioPack pack, byte[] inputBytes)
    {
        string schemaPath = ResolveContainedFile(pack.RootDirectory, pack.Manifest.InputSchemaRef);
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        using JsonDocument input = JsonDocument.Parse(inputBytes);
        SchemaValidationReport report = new FoundationSchemaValidator().Validate(schema.RootElement, input.RootElement);
        if (!report.IsValid)
        {
            string issues = string.Join("; ", report.Issues.Select(issue => $"{issue.JsonPath}:{issue.ReasonCode}"));
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ManifestInvalid, $"Pack input is invalid: {issues}");
        }
    }

    private static string ResolveContainedFile(string root, string reference)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, $"Pack reference is unavailable: {reference}.");
        }
        return candidate;
    }
}
