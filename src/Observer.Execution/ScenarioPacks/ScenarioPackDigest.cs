using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public static class ScenarioPackDigest
{
    public static string Compute(string packDirectory)
    {
        string root = Path.GetFullPath(packDirectory);
        string manifestPath = Path.Combine(root, ScenarioPackContract.ManifestFileName);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        JsonObject manifest = JsonNode.Parse(manifestBytes)?.AsObject()
            ?? throw new InvalidDataException("Scenario Pack manifest must be a JSON object.");
        if (!manifest.Remove("digest"))
        {
            throw new InvalidDataException("Scenario Pack manifest digest is missing.");
        }

        var files = new JsonArray();
        foreach (string file in EnumeratePayloadFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files.Add(new JsonObject
            {
                ["path"] = relative,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),
            });
        }

        var material = new JsonObject
        {
            ["digest_contract"] = "fs-observer/scenario-pack-digest/1",
            ["manifest_without_digest"] = manifest,
            ["payload_files"] = files,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(material);
        return FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(json));
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string root)
    {
        var files = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ScenarioPackLoadException(
                    ScenarioPackReasonCodes.ReferenceUnsafe,
                    $"Reparse points are forbidden inside a Scenario Pack: {Path.GetRelativePath(root, path)}.");
            }
            if ((attributes & FileAttributes.Directory) == 0 &&
                !string.Equals(Path.GetFullPath(path), Path.Combine(root, ScenarioPackContract.ManifestFileName), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFullPath(path), Path.Combine(root, ScenarioPackSignatureContract.SignatureFileName), StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Path.GetFullPath(path));
            }
        }
        return files.OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal);
    }
}
