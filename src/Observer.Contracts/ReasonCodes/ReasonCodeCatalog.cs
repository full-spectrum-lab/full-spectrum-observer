using System.Collections.Frozen;
using System.Text.Json;

namespace FullSpectrum.Observer.Contracts.ReasonCodes;

public sealed class ReasonCodeCatalog
{
    private readonly FrozenDictionary<string, string> _domainByCode;

    private ReasonCodeCatalog(Dictionary<string, string> domainByCode)
    {
        _domainByCode = domainByCode.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Count => _domainByCode.Count;
    public IReadOnlyCollection<string> Codes => _domainByCode.Keys;

    public bool Contains(string code) => _domainByCode.ContainsKey(code);

    public bool TryGetDomain(string code, out string domain)
    {
        if (_domainByCode.TryGetValue(code, out string? value) && value is not null)
        {
            domain = value;
            return true;
        }
        domain = string.Empty;
        return false;
    }

    public static ReasonCodeCatalog Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });

        JsonElement domains = document.RootElement.GetProperty("domains");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty domainProperty in domains.EnumerateObject())
        {
            string domain = domainProperty.Name;
            foreach (JsonElement item in domainProperty.Value.EnumerateArray())
            {
                string code = item.GetString() ?? throw new InvalidDataException("Reason code must be a string.");
                if (!code.StartsWith(domain + "_", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Reason code {code} does not match domain {domain}.");
                }
                if (!result.TryAdd(code, domain))
                {
                    throw new InvalidDataException($"Duplicate reason code: {code}.");
                }
            }
        }

        if (result.Count != 50)
        {
            throw new InvalidDataException($"Expected 50 reason codes, actual {result.Count}.");
        }

        return new ReasonCodeCatalog(result);
    }
}
