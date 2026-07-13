using System.Security.Cryptography;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Evidence;

public sealed class ContentAddressedArtifactStore : IArtifactStore
{
    private readonly EvidenceOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public ContentAddressedArtifactStore(EvidenceOptions options, IClock clock, IIdGenerator ids)
    {
        _options = options;
        _clock = clock;
        _ids = ids;
    }

    public async Task<ArtifactDescriptor> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken)
    {
        string digest = Convert.ToHexStringLower(SHA256.HashData(request.Content.Span));
        string relativePath = Path.Combine(digest[..2], digest).Replace('\\', '/');
        string finalPath = Path.Combine(_options.ArtifactRoot, digest[..2], digest);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        Directory.CreateDirectory(_options.TemporaryRoot);

        if (File.Exists(finalPath))
        {
            await VerifyExistingAsync(finalPath, digest, request.Content.Length, cancellationToken).ConfigureAwait(false);
            return Descriptor(request, digest, relativePath);
        }

        string tempPath = Path.Combine(_options.TemporaryRoot, $"{digest}.{Guid.NewGuid():N}.tmp");
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            await using (var stream = new FileStream(tempPath, fileOptions))
            {
                await stream.WriteAsync(request.Content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(tempPath);
            }

            await VerifyExistingAsync(finalPath, digest, request.Content.Length, cancellationToken).ConfigureAwait(false);
            return Descriptor(request, digest, relativePath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_WRITE_FAILED, "Artifact write failed.", exception);
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        string relative = artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(_options.ArtifactRoot, relative));
        string root = Path.GetFullPath(_options.ArtifactRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED, "Artifact path escapes the configured root.");
        await VerifyExistingAsync(path, artifact.Sha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task<bool> VerifyAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_options.ArtifactRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return false;
        try
        {
            await VerifyExistingAsync(path, artifact.Sha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (EvidenceStoreException)
        {
            return false;
        }
    }

    private ArtifactDescriptor Descriptor(ArtifactWriteRequest request, string digest, string relativePath) => new(
        _ids.NewId().ToString("D"),
        request.MediaType,
        digest,
        request.Content.Length,
        relativePath,
        request.Classification,
        _clock.UtcNow.ToString("O"));

    private static async Task VerifyExistingAsync(string path, string expectedDigest, long expectedSize, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedSize)
        {
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED, "Artifact size mismatch.");
        }
        await using FileStream stream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(digest), expectedDigest, StringComparison.Ordinal))
        {
            throw new EvidenceStoreException(FoundationReasonCodes.STORE_CORRUPTION_SUSPECTED, "Artifact digest mismatch.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
