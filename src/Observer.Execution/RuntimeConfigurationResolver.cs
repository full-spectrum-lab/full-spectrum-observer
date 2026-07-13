using System.Security.Cryptography;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

namespace FullSpectrum.Observer.Execution;

public sealed class RuntimeConfigurationResolver
{
    private readonly FoundationExecutionOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public RuntimeConfigurationResolver(FoundationExecutionOptions options, IClock clock, IIdGenerator ids)
    {
        _options = options;
        _clock = clock;
        _ids = ids;
    }

    public RuntimeConfigurationSnapshot Resolve()
    {
        string componentsPath = Path.Combine(_options.CasePackDirectory, "runtime-components.json");
        using JsonDocument components = JsonDocument.Parse(File.ReadAllBytes(componentsPath));
        JsonElement refs = components.RootElement.GetProperty("components");
        string packManifest = Path.Combine(_options.CasePackDirectory, "foundation-case-pack.manifest.json");
        string engineManifest = Path.Combine(_options.RepositoryRoot, "engine", "bridge-source-manifest.json");
        string schemaLock = Path.Combine(_options.SchemaDirectory, "schemas.lock.json");

        JsonElement ComponentRef(string name, string file)
        {
            JsonElement definition = refs.GetProperty(name);
            return JsonSerializer.SerializeToElement(new
            {
                id = definition.GetProperty("id").GetString(),
                version = definition.GetProperty("version").GetString(),
                schema_version = "1.0.0",
                sha256 = HashFile(file),
            }, FoundationJson.CreateOptions());
        }

        RuntimeConfigurationSnapshot snapshot = new()
        {
            Contract = "fs-observer/runtime-configuration-snapshot/1",
            SnapshotId = _ids.NewId().ToString("D"),
            CreatedAtUtc = _clock.UtcNow.ToString("O"),
            Engine = JsonSerializer.SerializeToElement(new
            {
                id = "full-spectrum-engine-source-bundle",
                version = BuildIdentity.EngineVersion,
                schema_version = "1.0.0",
                sha256 = HashFile(engineManifest),
                source_commit = BuildIdentity.EngineCommit,
            }, FoundationJson.CreateOptions()),
            Adapter = ComponentRef("adapter", componentsPath),
            Scenario = ComponentRef("scenario", packManifest),
            Profile = ComponentRef("profile", packManifest),
            Knowledge = ComponentRef("knowledge", packManifest),
            ReportTemplate = ComponentRef("report_template", packManifest),
            SchemaSet = JsonSerializer.SerializeToElement(new
            {
                id = BuildIdentity.SchemaBaseline,
                version = "1.0.0-alpha.1",
                schema_version = "2020-12",
                sha256 = HashFile(schemaLock),
            }, FoundationJson.CreateOptions()),
            Serialization = JsonSerializer.SerializeToElement(new
            {
                id = FsObsCanonicalizer.SerializationId,
                hash_algorithm = "SHA-256",
            }, FoundationJson.CreateOptions()),
            SnapshotSha256 = string.Empty,
        };
        string digest = SelfDigestCalculator.Compute(FoundationJson.Serialize(snapshot), "snapshot_sha256");
        RuntimeConfigurationSnapshot completed = snapshot with { SnapshotSha256 = digest };
        using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(_options.SchemaDirectory);
        SchemaValidationReport validation = bundle.Validate(
            "runtime-configuration-snapshot", FoundationJson.Serialize(completed));
        if (!validation.IsValid)
            throw new RuntimeSnapshotException(
                FoundationReasonCodes.SNAPSHOT_DIGEST_MISMATCH,
                "Runtime Configuration Snapshot failed the frozen Schema.");
        return completed;
    }

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

public sealed class RuntimeSnapshotException : IOException, IReasonCodedException
{
    public RuntimeSnapshotException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
