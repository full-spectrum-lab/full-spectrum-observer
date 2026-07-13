using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FullSpectrum.Observer.Contracts.Canonicalization;

public static class SelfDigestCalculator
{
    public static string Compute(ReadOnlySpan<byte> utf8Json, string excludedProperty)
    {
        JsonNode? node = JsonNode.Parse(utf8Json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        if (node is not JsonObject root)
        {
            throw new InvalidDataException("A self-containing digest can only be computed for a JSON object.");
        }
        if (!root.Remove(excludedProperty))
        {
            throw new InvalidDataException($"Excluded digest property is missing: {excludedProperty}.");
        }
        byte[] ordinaryJson = JsonSerializer.SerializeToUtf8Bytes(root);
        byte[] canonical = FsObsCanonicalizer.Canonicalize(ordinaryJson);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}
