using System.Text.Json;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Schema;
using FullSpectrum.Observer.Contracts.Serialization;

string root = RepositoryLayout.FindRoot();
string schemaDirectory = RepositoryLayout.SchemaDirectory(root);
string exampleDirectory = Path.Combine(schemaDirectory, "examples");

var tests = new List<(string Id, Action Execute)>
{
    ("TR-FK-CON-001", ValidateSchemaBundleAndExamples),
    ("TR-FK-CON-002", ValidateFoundationAnalysisRequestConditions),
    ("TR-FK-CON-003", ValidateSnapshotPinnedReferences),
    ("TR-FK-CON-004", ValidateFacadeProtocolConst),
    ("TR-FK-CON-001B", ValidateNegativeSchemaMappings),
};

int failures = 0;
foreach ((string id, Action execute) in tests)
{
    try
    {
        execute();
        Console.WriteLine($"PASS {id}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {id}: {exception.Message}");
    }
}
return failures == 0 ? 0 : 1;

void ValidateSchemaBundleAndExamples()
{
    using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(schemaDirectory);
    Assert(bundle.Count == 12, "Expected 12 schemas.");
    foreach (string name in bundle.Names.OrderBy(static value => value, StringComparer.Ordinal))
    {
        string examplePath = Path.Combine(exampleDirectory, name + ".example.json");
        byte[] example = File.ReadAllBytes(examplePath);
        SchemaValidationReport report = bundle.Validate(name, example);
        Assert(report.IsValid, $"{name}: {string.Join("; ", report.Issues)}");
    }

    var contractTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["audit-event"] = typeof(AuditEventContract),
        ["engine-facade-request"] = typeof(EngineFacadeRequest),
        ["engine-facade-response"] = typeof(EngineFacadeResponse),
        ["foundation-analysis-request"] = typeof(FoundationAnalysisRequest),
        ["foundation-case-pack-manifest"] = typeof(FoundationCasePackManifest),
        ["foundation-error-envelope"] = typeof(FoundationErrorEnvelope),
        ["foundation-operation-status"] = typeof(FoundationOperationStatus),
        ["governance-output-envelope"] = typeof(GovernanceOutputEnvelope),
        ["observation-record"] = typeof(ObservationRecord),
        ["release-manifest"] = typeof(ReleaseManifest),
        ["runtime-configuration-snapshot"] = typeof(RuntimeConfigurationSnapshot),
        ["validation-result"] = typeof(ValidationResultContract),
    };
    foreach ((string name, Type contractType) in contractTypes)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(exampleDirectory, name + ".example.json"));
        object? value = JsonSerializer.Deserialize(bytes, contractType, FoundationJson.CreateOptions());
        Assert(value is not null, $"DTO deserialization returned null: {name}.");
    }
}

void ValidateFoundationAnalysisRequestConditions()
{
    using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(schemaDirectory);
    const string missingCaseId = """
    {
      "contract":"fs-observer/foundation-analysis-request/1",
      "request_id":"12345678-1234-4234-8234-123456789abc",
      "idempotency_key":"key",
      "input":{"kind":"BUILTIN_CASE"},
      "requested_runtime":{"case_pack_id":"foundation","case_pack_version":"1.0.0","seed":42,"fixed_time_utc":"2026-07-04T00:00:00Z"},
      "submitted_at_utc":"2026-07-12T00:00:00Z"
    }
    """;
    SchemaValidationReport report = bundle.Validate("foundation-analysis-request", System.Text.Encoding.UTF8.GetBytes(missingCaseId));
    Assert(!report.IsValid, "Missing case_id must fail.");
    Assert(report.Issues.Any(issue => issue.ReasonCode == FoundationReasonCodes.SCHEMA_REQUIRED_MISSING), "Expected missing-required reason code.");
}

void ValidateSnapshotPinnedReferences()
{
    using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(schemaDirectory);
    string path = Path.Combine(exampleDirectory, "runtime-configuration-snapshot.example.json");
    JsonNodeRoot rootNode = JsonNodeRoot.Load(path);
    byte[] original = rootNode.ToUtf8Bytes();
    using JsonDocument originalDocument = JsonDocument.Parse(original);
    Assert(PinnedReferencePolicy.ValidateRuntimeSnapshot(originalDocument.RootElement).Count == 0, "Baseline snapshot must contain pinned references.");

    rootNode.Root.Remove("engine");
    byte[] missing = rootNode.ToUtf8Bytes();
    SchemaValidationReport missingReport = bundle.Validate("runtime-configuration-snapshot", missing);
    Assert(missingReport.Issues.Any(issue => issue.ReasonCode == FoundationReasonCodes.SCHEMA_REQUIRED_MISSING), "Missing engine reference must fail schema validation.");

    rootNode = JsonNodeRoot.Load(path);
    ((System.Text.Json.Nodes.JsonObject)rootNode.Root["engine"]!)["version"] = "latest";
    using JsonDocument floatingDocument = JsonDocument.Parse(rootNode.ToUtf8Bytes());
    Assert(PinnedReferencePolicy.ValidateRuntimeSnapshot(floatingDocument.RootElement)
        .Any(issue => issue.ReasonCode == FoundationReasonCodes.SNAPSHOT_FLOATING_VERSION_FORBIDDEN), "Floating version must fail pinned-reference policy.");
}

void ValidateFacadeProtocolConst()
{
    using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(schemaDirectory);
    JsonNodeRoot rootNode = JsonNodeRoot.Load(Path.Combine(exampleDirectory, "engine-facade-request.example.json"));
    rootNode.Root["protocol"] = "wrong-protocol";
    SchemaValidationReport report = bundle.Validate("engine-facade-request", rootNode.ToUtf8Bytes());
    Assert(report.Issues.Any(issue => issue.ReasonCode == FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE), "Wrong protocol const must fail.");
}

void ValidateNegativeSchemaMappings()
{
    using FoundationSchemaBundle bundle = FoundationSchemaBundle.Load(schemaDirectory);
    JsonNodeRoot rootNode = JsonNodeRoot.Load(Path.Combine(exampleDirectory, "foundation-analysis-request.example.json"));
    rootNode.Root["unexpected"] = true;
    SchemaValidationReport additional = bundle.Validate("foundation-analysis-request", rootNode.ToUtf8Bytes());
    Assert(additional.Issues.Any(issue => issue.ReasonCode == FoundationReasonCodes.SCHEMA_ADDITIONAL_PROPERTY), "Additional property mapping failed.");

    rootNode = JsonNodeRoot.Load(Path.Combine(exampleDirectory, "foundation-analysis-request.example.json"));
    rootNode.Root["timeout_seconds"] = "thirty";
    SchemaValidationReport type = bundle.Validate("foundation-analysis-request", rootNode.ToUtf8Bytes());
    Assert(type.Issues.Any(issue => issue.ReasonCode == FoundationReasonCodes.SCHEMA_TYPE_INVALID), "Type-invalid mapping failed.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class JsonNodeRoot
{
    public System.Text.Json.Nodes.JsonObject Root { get; }
    private JsonNodeRoot(System.Text.Json.Nodes.JsonObject root) => Root = root;
    public static JsonNodeRoot Load(string path)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path)) as System.Text.Json.Nodes.JsonObject;
        return new JsonNodeRoot(node ?? throw new InvalidDataException("Expected object example."));
    }
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(Root);
}
