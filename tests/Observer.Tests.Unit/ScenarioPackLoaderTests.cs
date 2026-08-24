using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class ScenarioPackLoaderTests
{
    private const string ObserverVersion = "v0.4.0-beta";
    private const string EngineVersion = "v1.5.0";

    [Fact]
    public void Same_loader_accepts_two_distinct_synthetic_packs()
    {
        string root = FindRepositoryRoot();
        var loader = new ScenarioPackLoader(
            Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
            Path.Combine(root, "config", "scenario-pack-trust-roots.json"),
            ObserverVersion,
            EngineVersion);

        LoadedScenarioPack knowledgeConflict = loader.Load(
            Path.Combine(root, "packs", "scenario-packs", "knowledge-conflict", "1.0.0"));
        LoadedScenarioPack policyConflict = loader.Load(
            Path.Combine(root, "packs", "scenario-packs", "synthetic-policy-conflict", "1.0.0"));

        knowledgeConflict.Identity.PackId.Should().Be("observer.case.knowledge-conflict");
        policyConflict.Identity.PackId.Should().Be("observer.case.synthetic-policy-conflict");
        knowledgeConflict.Manifest.SubjectTypes.Should().NotEqual(policyConflict.Manifest.SubjectTypes);
        knowledgeConflict.Manifest.Contract.Should().Be(ScenarioPackContract.ManifestV1);
        policyConflict.Manifest.Contract.Should().Be(ScenarioPackContract.ManifestV1);
    }

    [Fact]
    public void Changed_payload_is_rejected_when_manifest_digest_is_not_updated()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        File.AppendAllText(Path.Combine(pack.Path, "golden", "golden-001.input.json"), " ");

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.DigestMismatch);
    }

    [Fact]
    public void Incompatible_observer_version_fails_closed()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        pack.RewriteManifest(root => root["compatible_observer_versions"] = new JsonArray("v9.0.0"));

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.ObserverIncompatible);
    }

    [Fact]
    public void Incompatible_engine_version_fails_closed()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        pack.RewriteManifest(root => root["compatible_engine_versions"] = new JsonArray("v2.0.0"));

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.EngineIncompatible);
    }

    [Fact]
    public void Reference_that_escapes_pack_root_is_rejected()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        pack.RewriteManifest(root => root["input_schema_ref"] = "../outside.schema.json");

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.ReferenceUnsafe);
    }

    [Fact]
    public void Unknown_manifest_field_is_rejected_instead_of_silently_dropped()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        pack.RewriteManifest(root => root["future_field"] = true);

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.ManifestInvalid);
    }

    [Fact]
    public void Registry_freezes_pack_id_version_and_digest()
    {
        string root = FindRepositoryRoot();
        var loader = CreateLoader(Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"));
        LoadedScenarioPack original = loader.Load(
            Path.Combine(root, "packs", "scenario-packs", "knowledge-conflict", "1.0.0"));
        var registry = new ScenarioPackRegistry();

        ScenarioPackIdentity first = registry.Install(original);
        ScenarioPackIdentity repeated = registry.Install(original);

        repeated.Should().Be(first);
        registry.Resolve(first.PackId, first.Version).Identity.Should().Be(first);

    }

    [Fact]
    public void Missing_detached_signature_is_rejected()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        File.Delete(Path.Combine(pack.Path, ScenarioPackSignatureContract.SignatureFileName));

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.SignatureMissing);
    }

    [Fact]
    public void Invalid_detached_signature_is_rejected()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        string path = Path.Combine(pack.Path, ScenarioPackSignatureContract.SignatureFileName);
        JsonObject signature = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        signature["signature_base64"] = Convert.ToBase64String(new byte[256]);
        File.WriteAllText(path, signature.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.SignatureInvalid);
    }

    [Fact]
    public void Changed_digest_without_a_new_trusted_signature_is_rejected()
    {
        using var pack = TemporaryPack.CopyFrom("knowledge-conflict");
        File.AppendAllText(Path.Combine(pack.Path, "reviews", "human-review-template.md"), "\nSynthetic revision.\n");
        pack.RefreshDigest();

        Action load = () => CreateLoader(pack.SchemaPath).Load(pack.Path);

        load.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.SignatureInvalid);
    }

    [Fact]
    public void Unknown_trust_store_field_is_rejected()
    {
        string root = FindRepositoryRoot();
        string source = Path.Combine(root, "config", "scenario-pack-trust-roots.json");
        string temporary = Path.Combine(Path.GetTempPath(), $"observer-v04-trust-{Guid.NewGuid():N}.json");
        try
        {
            JsonObject trustStore = JsonNode.Parse(File.ReadAllBytes(source))!.AsObject();
            trustStore["future_field"] = true;
            File.WriteAllText(temporary, trustStore.ToJsonString());

            Action create = () => _ = new ScenarioPackLoader(
                Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
                temporary, ObserverVersion, EngineVersion);

            create.Should().Throw<ScenarioPackLoadException>()
                .Where(error => error.ReasonCode == ScenarioPackReasonCodes.TrustRootMissing);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ScenarioPackLoader CreateLoader(string schemaPath) =>
        new(schemaPath, Path.Combine(FindRepositoryRoot(), "config", "scenario-pack-trust-roots.json"), ObserverVersion, EngineVersion);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "baselines.lock.json")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TemporaryPack : IDisposable
    {
        private readonly string _root;
        public string Path { get; }
        public string SchemaPath { get; }

        private TemporaryPack(string root, string path, string schemaPath)
        {
            _root = root;
            Path = path;
            SchemaPath = schemaPath;
        }

        public static TemporaryPack CopyFrom(string packName)
        {
            string repository = FindRepositoryRoot();
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"observer-v04-pack-{Guid.NewGuid():N}");
            string pack = System.IO.Path.Combine(root, "pack");
            CopyDirectory(System.IO.Path.Combine(repository, "packs", "scenario-packs", packName, "1.0.0"), pack);
            return new TemporaryPack(
                root,
                pack,
                System.IO.Path.Combine(repository, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"));
        }

        public void RewriteManifest(Action<JsonObject> change)
        {
            string path = System.IO.Path.Combine(Path, ScenarioPackContract.ManifestFileName);
            JsonObject root = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject()
                ?? throw new InvalidDataException("Manifest must be a JSON object.");
            change(root);
            root["digest"] = new string('0', 64);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            root["digest"] = ScenarioPackDigest.Compute(Path);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }

        public void RefreshDigest()
        {
            RewriteManifest(_ => { });
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file));
                File.Copy(file, target);
            }
        }
    }
}
