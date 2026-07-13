using System.Security.Cryptography;
using System.Text.Json;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Execution;

public sealed class FoundationInputIntake
{
    private readonly FoundationExecutionOptions _options;

    public FoundationInputIntake(FoundationExecutionOptions options)
    {
        options.Validate();
        _options = options;
    }

    public async Task<IntakeResult> LoadAsync(JsonElement input, CancellationToken cancellationToken)
    {
        string kind = input.GetProperty("kind").GetString()
            ?? throw new IntakeException(FoundationReasonCodes.REQ_INVALID_ARGUMENT, "Input kind is missing.");

        string path;
        string? expectedSha256 = input.TryGetProperty("expected_sha256", out JsonElement expected)
            ? expected.GetString()
            : null;

        if (kind == "BUILTIN_CASE")
        {
            string caseId = input.GetProperty("case_id").GetString() ?? string.Empty;
            if (!string.Equals(caseId, "CASE005_KNOWLEDGE_CONFLICT", StringComparison.Ordinal))
                throw new IntakeException(FoundationReasonCodes.ADAPTER_CASE_NOT_FOUND, "Only the frozen CASE005 reference is available.");
            path = Path.Combine(_options.CasePackDirectory, "case005.input.json");
        }
        else if (kind == "JSON_FILE")
        {
            string supplied = input.GetProperty("file_path").GetString() ?? string.Empty;
            path = ResolveInputPath(supplied);
        }
        else
        {
            throw new IntakeException(FoundationReasonCodes.REQ_INVALID_ARGUMENT, "Unsupported input kind.");
        }

        if (!File.Exists(path))
            throw new IntakeException(FoundationReasonCodes.INTAKE_FILE_NOT_FOUND, "Input file does not exist.");

        FileInfo info = new(path);
        if (info.Length > _options.MaximumInputBytes)
            throw new IntakeException(FoundationReasonCodes.INTAKE_FILE_TOO_LARGE, "Input file exceeds the configured limit.");

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(expectedSha256, digest, StringComparison.Ordinal))
            throw new IntakeException(FoundationReasonCodes.INTAKE_HASH_MISMATCH, "Input digest does not match expected_sha256.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Scenario root must be an object.");
            return new IntakeResult(bytes, document.RootElement.Clone(), digest, kind);
        }
        catch (JsonException exception)
        {
            throw new IntakeException(FoundationReasonCodes.INTAKE_JSON_PARSE_FAILED, "Input is not valid scenario JSON.", exception);
        }
    }

    private string ResolveInputPath(string supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied))
            throw new IntakeException(FoundationReasonCodes.REQ_INVALID_ARGUMENT, "file_path is required.");
        if (supplied.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IntakeException(FoundationReasonCodes.INTAKE_PATH_OUTSIDE_ALLOWED_ROOT, "UNC paths are forbidden.");

        string root = Path.GetFullPath(_options.AllowedInputRoot) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.IsPathFullyQualified(supplied)
            ? supplied
            : Path.Combine(_options.AllowedInputRoot, supplied));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IntakeException(FoundationReasonCodes.INTAKE_PATH_OUTSIDE_ALLOWED_ROOT, "Input path escapes the allowed root.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IntakeException(FoundationReasonCodes.INTAKE_PATH_OUTSIDE_ALLOWED_ROOT, "Reparse-point inputs are forbidden.");
        return path;
    }
}

public sealed class IntakeException : IOException, IReasonCodedException
{
    public IntakeException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
