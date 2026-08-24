using System.Text.Json.Serialization;

namespace FullSpectrum.Observer.Contracts.Models;

public static class ScenarioPackSignatureContract
{
    public const string SignatureV1 = "fs-observer/scenario-pack-signature/1";
    public const string TrustStoreV1 = "fs-observer/scenario-pack-trust-store/1";
    public const string SignatureFileName = "scenario-pack.signature.json";
    public const string AlgorithmRs256 = "RS256";
}

public sealed record ScenarioPackSignatureEnvelope
{
    [JsonPropertyName("contract")] public required string Contract { get; init; }
    [JsonPropertyName("key_id")] public required string KeyId { get; init; }
    [JsonPropertyName("algorithm")] public required string Algorithm { get; init; }
    [JsonPropertyName("pack_id")] public required string PackId { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("digest")] public required string Digest { get; init; }
    [JsonPropertyName("signature_base64")] public required string SignatureBase64 { get; init; }
}

public sealed record ScenarioPackTrustStoreDocument
{
    [JsonPropertyName("contract")] public required string Contract { get; init; }
    [JsonPropertyName("keys")] public required ScenarioPackTrustRoot[] Keys { get; init; }
}

public sealed record ScenarioPackTrustRoot
{
    [JsonPropertyName("key_id")] public required string KeyId { get; init; }
    [JsonPropertyName("algorithm")] public required string Algorithm { get; init; }
    [JsonPropertyName("modulus_base64url")] public required string ModulusBase64Url { get; init; }
    [JsonPropertyName("exponent_base64url")] public required string ExponentBase64Url { get; init; }
    [JsonPropertyName("purpose")] public required string Purpose { get; init; }
}
