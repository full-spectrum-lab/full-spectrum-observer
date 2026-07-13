using System.Security.Cryptography;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.EngineFacade;

internal sealed record WorkerFileLock(string Path, long SizeBytes, string Sha256);
internal sealed record WorkerLockManifest(string Protocol, string EngineVersion, string EngineCommit, IReadOnlyList<WorkerFileLock> Files);

internal static class WorkerIntegrityVerifier
{
    public static WorkerLockManifest Verify(string lockPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        JsonElement root = document.RootElement;
        string protocol = root.GetProperty("protocol").GetString() ?? string.Empty;
        string engineVersion = root.GetProperty("engine_version").GetString() ?? string.Empty;
        string engineCommit = root.GetProperty("engine_commit").GetString() ?? string.Empty;
        if (!string.Equals(protocol, "fs-observer-worker-lock/1", StringComparison.Ordinal))
            throw new EngineFacadeException(FoundationReasonCodes.FACADE_PROTOCOL_INVALID, "Worker integrity lock protocol is invalid.");
        string baseDirectory = Path.GetFullPath(Path.GetDirectoryName(lockPath)!);
        string basePrefix = baseDirectory.EndsWith(Path.DirectorySeparatorChar) ? baseDirectory : baseDirectory + Path.DirectorySeparatorChar;
        var files = new List<WorkerFileLock>();
        foreach (JsonElement element in root.GetProperty("files").EnumerateArray())
        {
            var item = new WorkerFileLock(
                element.GetProperty("path").GetString() ?? string.Empty,
                element.GetProperty("size_bytes").GetInt64(),
                element.GetProperty("sha256").GetString() ?? string.Empty);
            string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if ((!string.Equals(fullPath, baseDirectory, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)) || !File.Exists(fullPath))
                throw new EngineFacadeException(FoundationReasonCodes.FACADE_WORKER_NOT_FOUND, $"Pinned Worker file is missing: {item.Path}.");
            var info = new FileInfo(fullPath);
            using FileStream stream = File.OpenRead(fullPath);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (info.Length != item.SizeBytes || !string.Equals(actual, item.Sha256, StringComparison.Ordinal))
                throw new EngineFacadeException(FoundationReasonCodes.FACADE_WORKER_HASH_MISMATCH, $"Pinned Worker file hash mismatch: {item.Path}.");
            files.Add(item);
        }
        return new WorkerLockManifest(protocol, engineVersion, engineCommit, files);
    }
}
