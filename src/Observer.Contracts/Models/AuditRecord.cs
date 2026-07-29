namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// An append-only audit record. Red line #7: the underlying table accepts INSERT only;
/// this type exposes no mutating operations and the store rejects any UPDATE/DELETE.
/// Records form a chain via <see cref="PrevAuditId"/>. The Windows user / machine / session
/// fields are audit context ONLY and are never used as a login identity (red line #1).
/// </summary>
public sealed record AuditRecord
{
    /// <summary>Audit identifier, e.g. <c>AUD-2026-0007-0001</c>.</summary>
    public required string AuditId { get; init; }

    /// <summary>Related task identifier; null for system-level audit events.</summary>
    public string? TaskId { get; init; }

    /// <summary>Audit action, e.g. <c>CREATE_SUBJECT</c> / <c>ACTIVATE</c> / <c>RUN</c> / <c>RESULT</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Windows user / SID. Audit context only.</summary>
    public required string WindowsUser { get; init; }

    /// <summary>Machine name. Audit context only.</summary>
    public required string Machine { get; init; }

    /// <summary>Session identifier. Audit context only.</summary>
    public required string Session { get; init; }

    /// <summary>Event timestamp (ISO-8601 UTC). Immutable.</summary>
    public required string At { get; init; }

    /// <summary>Event digest (chained). Immutable.</summary>
    public required string Digest { get; init; }

    /// <summary>Previous audit identifier (chain pointer); null for the first record.</summary>
    public string? PrevAuditId { get; init; }

    /// <summary>
    /// Factory that appends a new audit record chained to <paramref name="previousAuditId"/>.
    /// This is a pure projection: the store assigns the final digest and persists via INSERT only.
    /// </summary>
    public static AuditRecord Append(
        string auditId,
        string? taskId,
        string action,
        string windowsUser,
        string machine,
        string session,
        string atUtc,
        string digest,
        string? previousAuditId) => new()
    {
        AuditId = auditId,
        TaskId = taskId,
        Action = action,
        WindowsUser = windowsUser,
        Machine = machine,
        Session = session,
        At = atUtc,
        Digest = digest,
        PrevAuditId = previousAuditId,
    };
}
