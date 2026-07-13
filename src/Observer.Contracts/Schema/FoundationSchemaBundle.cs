using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace FullSpectrum.Observer.Contracts.Schema;

public sealed class FoundationSchemaBundle : IDisposable
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string BaselineStatus = "APPROVED_DESIGN_BASELINE";
    private const string SchemaSet = "FS-OBS-V010-SCHEMA-BL-1.0";

    private readonly FrozenDictionary<string, JsonDocument> _schemas;

    private FoundationSchemaBundle(Dictionary<string, JsonDocument> schemas)
    {
        _schemas = schemas.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Count => _schemas.Count;
    public IEnumerable<string> Names => _schemas.Keys;

    public JsonElement GetSchema(string name)
    {
        if (!_schemas.TryGetValue(name, out JsonDocument? document) || document is null)
        {
            throw new KeyNotFoundException($"Unknown Foundation Schema: {name}.");
        }
        return document.RootElement;
    }

    public SchemaValidationReport Validate(string name, ReadOnlySpan<byte> instanceJson)
    {
        using JsonDocument instance = JsonDocument.Parse(instanceJson.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        return new FoundationSchemaValidator().Validate(GetSchema(name), instance.RootElement);
    }

    public static FoundationSchemaBundle Load(string directory)
    {
        string lockPath = Path.Combine(directory, "schemas.lock.json");
        using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        JsonElement files = lockDocument.RootElement.GetProperty("files");

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement file in files.EnumerateArray())
        {
            string name = file.GetProperty("file").GetString() ?? throw new InvalidDataException("Schema lock file name missing.");
            string expectedHash = file.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Schema lock hash missing.");
            if (Path.IsPathRooted(name) || name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static part => part == ".."))
            {
                throw new InvalidDataException($"Unsafe locked path: {name}.");
            }
            expected.Add(name, expectedHash);
        }

        foreach ((string fileName, string expectedHash) in expected)
        {
            string path = Path.GetFullPath(Path.Combine(directory, fileName.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Locked path escapes schema directory: {fileName}.");
            }
            byte[] bytes = File.ReadAllBytes(path);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Locked file digest mismatch: {fileName}.");
            }
        }

        string[] schemaFiles = expected.Keys.Where(static name => name.EndsWith(".schema.json", StringComparison.Ordinal)).ToArray();
        if (schemaFiles.Length != 12)
        {
            throw new InvalidDataException($"Expected 12 locked schemas, actual {schemaFiles.Length}.");
        }

        var documents = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string fileName in schemaFiles)
            {
                string path = Path.Combine(directory, fileName.Replace('/', Path.DirectorySeparatorChar));
                byte[] bytes = File.ReadAllBytes(path);
                JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
                JsonElement root = document.RootElement;
                string draft = root.GetProperty("$schema").GetString() ?? string.Empty;
                string id = root.GetProperty("$id").GetString() ?? string.Empty;
                string status = root.GetProperty("x-fsp-status").GetString() ?? string.Empty;
                string schemaSet = root.GetProperty("x-fsp-schema-set").GetString() ?? string.Empty;

                if (draft != Draft202012 || status != BaselineStatus || schemaSet != SchemaSet || id.Length == 0)
                {
                    document.Dispose();
                    throw new InvalidDataException($"Schema identity or status invalid: {fileName}.");
                }
                if (!ids.Add(id))
                {
                    document.Dispose();
                    throw new InvalidDataException($"Duplicate schema $id: {id}.");
                }
                CommonFragmentConsistencyValidator.Validate(root);
                documents.Add(fileName.Replace(".schema.json", string.Empty, StringComparison.Ordinal), document);
            }
            return new FoundationSchemaBundle(documents);
        }
        catch
        {
            foreach (JsonDocument document in documents.Values)
            {
                document.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        foreach (JsonDocument document in _schemas.Values)
        {
            document.Dispose();
        }
    }
}
