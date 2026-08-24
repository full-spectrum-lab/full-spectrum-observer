using System.Security.Cryptography;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public sealed class ScenarioPackLoader
{
    private readonly string _schemaPath;
    private readonly IReadOnlyDictionary<string, ScenarioPackTrustRoot> _trustRoots;
    private readonly string _observerVersion;
    private readonly string _engineVersion;

    public ScenarioPackLoader(string schemaPath, string trustStorePath, string observerVersion, string engineVersion)
    {
        _schemaPath = Path.GetFullPath(schemaPath);
        _trustRoots = LoadTrustRoots(trustStorePath);
        _observerVersion = RequireVersion(observerVersion, nameof(observerVersion));
        _engineVersion = RequireVersion(engineVersion, nameof(engineVersion));
    }

    public LoadedScenarioPack Load(string packDirectory)
    {
        string root = Path.GetFullPath(packDirectory);
        if (!Directory.Exists(root))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceMissing, "Pack directory does not exist.");
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, "Pack root cannot be a reparse point.");
        }

        string manifestPath = Path.Combine(root, ScenarioPackContract.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceMissing, $"{ScenarioPackContract.ManifestFileName} is missing.");
        }

        try
        {
            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            ValidateManifestSchema(manifestBytes);
            ScenarioPackManifest manifest = FoundationJson.Deserialize<ScenarioPackManifest>(manifestBytes);

            ValidateUniqueValues(manifest);
            ValidateReferences(root, manifest);

            string actualDigest = ScenarioPackDigest.Compute(root);
            if (!string.Equals(manifest.Digest, actualDigest, StringComparison.Ordinal))
            {
                throw new ScenarioPackLoadException(
                    ScenarioPackReasonCodes.DigestMismatch,
                    $"Declared digest {manifest.Digest} does not match computed digest {actualDigest}.");
            }

            RequireExactCompatibility(manifest.CompatibleObserverVersions, _observerVersion, ScenarioPackReasonCodes.ObserverIncompatible, "Observer");
            RequireExactCompatibility(manifest.CompatibleEngineVersions, _engineVersion, ScenarioPackReasonCodes.EngineIncompatible, "Engine");
            VerifySignature(root, manifest);

            var identity = new ScenarioPackIdentity(manifest.PackId, manifest.Version, manifest.Digest);
            return new LoadedScenarioPack(identity, manifest, root);
        }
        catch (ScenarioPackLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ManifestInvalid, "Pack validation failed.", exception);
        }
    }

    private void VerifySignature(string root, ScenarioPackManifest manifest)
    {
        string path = Path.Combine(root, ScenarioPackSignatureContract.SignatureFileName);
        if (!File.Exists(path))
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.SignatureMissing, "Detached Scenario Pack signature is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, "Detached Scenario Pack signature cannot be a reparse point.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        RequireExactProperties(document.RootElement, ScenarioPackReasonCodes.SignatureInvalid,
            "algorithm", "contract", "digest", "key_id", "pack_id", "signature_base64", "version");
        ScenarioPackSignatureEnvelope envelope = FoundationJson.Deserialize<ScenarioPackSignatureEnvelope>(File.ReadAllBytes(path));
        if (envelope.Contract != ScenarioPackSignatureContract.SignatureV1 ||
            envelope.Algorithm != ScenarioPackSignatureContract.AlgorithmRs256 ||
            envelope.PackId != manifest.PackId || envelope.Version != manifest.Version || envelope.Digest != manifest.Digest)
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.SignatureInvalid, "Detached signature identity does not match the manifest.");
        if (!_trustRoots.TryGetValue(envelope.KeyId, out ScenarioPackTrustRoot? trustRoot) ||
            trustRoot.Algorithm != envelope.Algorithm)
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.TrustRootMissing, $"No trusted key is configured for {envelope.KeyId}.");

        byte[] material = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            algorithm = envelope.Algorithm,
            contract = envelope.Contract,
            digest = envelope.Digest,
            key_id = envelope.KeyId,
            pack_id = envelope.PackId,
            version = envelope.Version,
        }));
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = DecodeBase64Url(trustRoot.ModulusBase64Url),
                Exponent = DecodeBase64Url(trustRoot.ExponentBase64Url),
            });
            if (!rsa.VerifyData(material, Convert.FromBase64String(envelope.SignatureBase64),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new ScenarioPackLoadException(ScenarioPackReasonCodes.SignatureInvalid, "Detached Scenario Pack signature verification failed.");
        }
        catch (ScenarioPackLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.SignatureInvalid, "Detached Scenario Pack signature is malformed.", exception);
        }
    }

    private static IReadOnlyDictionary<string, ScenarioPackTrustRoot> LoadTrustRoots(string trustStorePath)
    {
        string path = Path.GetFullPath(trustStorePath);
        if (!File.Exists(path))
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.TrustRootMissing, "Scenario Pack trust store is missing.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        RequireExactProperties(document.RootElement, ScenarioPackReasonCodes.TrustRootMissing, "contract", "keys");
        JsonElement keysElement = document.RootElement.GetProperty("keys");
        if (keysElement.ValueKind != JsonValueKind.Array)
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.TrustRootMissing, "Scenario Pack trust-store keys must be an array.");
        foreach (JsonElement keyElement in keysElement.EnumerateArray())
        {
            RequireExactProperties(keyElement, ScenarioPackReasonCodes.TrustRootMissing,
                "algorithm", "exponent_base64url", "key_id", "modulus_base64url", "purpose");
        }
        ScenarioPackTrustStoreDocument store = FoundationJson.Deserialize<ScenarioPackTrustStoreDocument>(File.ReadAllBytes(path));
        if (store.Contract != ScenarioPackSignatureContract.TrustStoreV1 || store.Keys.Length == 0 ||
            store.Keys.Any(key => key.Algorithm != ScenarioPackSignatureContract.AlgorithmRs256 || string.IsNullOrWhiteSpace(key.KeyId)) ||
            store.Keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != store.Keys.Length)
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.TrustRootMissing, "Scenario Pack trust store is invalid.");
        return store.Keys.ToDictionary(key => key.KeyId, StringComparer.Ordinal);
    }

    private static void RequireExactProperties(JsonElement element, string reasonCode, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ScenarioPackLoadException(reasonCode, "Signature or trust metadata contains missing or unknown fields.");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private void ValidateManifestSchema(byte[] manifestBytes)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(File.ReadAllBytes(_schemaPath));
        using JsonDocument manifestDocument = JsonDocument.Parse(manifestBytes);
        SchemaValidationReport report = new FoundationSchemaValidator().Validate(schemaDocument.RootElement, manifestDocument.RootElement);
        if (!report.IsValid)
        {
            string issues = string.Join("; ", report.Issues.Select(issue => $"{issue.JsonPath}:{issue.ReasonCode}"));
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ManifestInvalid, issues);
        }
    }

    private static void ValidateReferences(string root, ScenarioPackManifest manifest)
    {
        var references = new List<string>
        {
            manifest.InputSchemaRef,
            manifest.HumanReviewTemplate,
        };
        references.AddRange(manifest.PolicyProfileRefs);
        references.AddRange(manifest.GoldenCases.Select(item => item.InputRef));
        foreach (string reference in references.Distinct(StringComparer.Ordinal))
        {
            _ = ResolveContainedFile(root, reference);
        }

        string inputSchemaPath = ResolveContainedFile(root, manifest.InputSchemaRef);
        using JsonDocument inputSchema = JsonDocument.Parse(File.ReadAllBytes(inputSchemaPath));
        if (inputSchema.RootElement.ValueKind != JsonValueKind.Object ||
            !inputSchema.RootElement.TryGetProperty("$schema", out _))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ManifestInvalid, "input_schema_ref must identify a JSON Schema document.");
        }
    }

    private static string ResolveContainedFile(string root, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains('\\'))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, $"Unsafe Pack reference: {reference}.");
        }
        string[] parts = reference.Split('/', StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, $"Unsafe Pack reference: {reference}.");
        }

        string candidate = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, $"Pack reference escapes its root: {reference}.");
        }
        if (!File.Exists(candidate))
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceMissing, $"Referenced Pack file is missing: {reference}.");
        }
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ReferenceUnsafe, $"Referenced Pack file is a reparse point: {reference}.");
        }
        return candidate;
    }

    private static void RequireExactCompatibility(string[] versions, string actual, string reasonCode, string component)
    {
        if (!versions.Contains(actual, StringComparer.Ordinal))
        {
            throw new ScenarioPackLoadException(reasonCode, $"{component} version {actual} is not explicitly allowed by the Pack.");
        }
    }

    private static void ValidateUniqueValues(ScenarioPackManifest manifest)
    {
        RequireUnique(manifest.CompatibleObserverVersions, "compatible_observer_versions");
        RequireUnique(manifest.CompatibleEngineVersions, "compatible_engine_versions");
        RequireUnique(manifest.SubjectTypes, "subject_types");
        RequireUnique(manifest.KnowledgeRequirements, "knowledge_requirements");
        RequireUnique(manifest.PolicyProfileRefs, "policy_profile_refs");
        RequireUnique(manifest.GoldenCases.Select(item => item.CaseId), "golden_cases.case_id");
        RequireUnique(manifest.ValueMetrics.Select(item => item.MetricId), "value_metrics.metric_id");
    }

    private static void RequireUnique(IEnumerable<string> values, string field)
    {
        string[] materialized = values.ToArray();
        if (materialized.Any(string.IsNullOrWhiteSpace) || materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ScenarioPackLoadException(ScenarioPackReasonCodes.ManifestInvalid, $"{field} must contain unique, non-blank values.");
        }
    }

    private static string RequireVersion(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Version is required.", parameterName) : value;
}
