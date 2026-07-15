using System.Text.Json;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// A versioned declaration of an <see cref="ObservedSubject"/>.
/// Versioned entities follow ADR-001: an immutable lifecycle Draft -> Active -> Retired.
/// An <c>Active</c> row is immutable (DB CHECK + application guard). Editing means
/// creating a new Draft, then activating it which produces a new immutable Active version.
/// </summary>
public sealed record SubjectVersion
{
    /// <summary>Version identifier, e.g. <c>SUBV-2026-0007-A1</c>.</summary>
    public required string VersionId { get; init; }

    /// <summary>Owning subject identifier (FK to <c>subjects.local_subject_id</c>).</summary>
    public required string SubjectId { get; init; }

    /// <summary>Lifecycle status: <c>Draft</c> / <c>Active</c> / <c>Retired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Monotonic sequence within the same subject (starts at 1).</summary>
    public required int Seq { get; init; }

    /// <summary>Payload JSON: the declaration object {display_name, boundary, owner_operator, ...}.</summary>
    public required string Payload { get; init; }

    /// <summary>Observer Schema version this version was authored against.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Version creation timestamp (ISO-8601 UTC).</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Activation timestamp; populated when status becomes <c>Active</c>.</summary>
    public string? ActiveFrom { get; init; }

    /// <summary>Retirement timestamp; populated when status becomes <c>Retired</c>.</summary>
    public string? RetiredAt { get; init; }

    /// <summary>Returns true when this version is the immutable Active version.</summary>
    public bool IsActive() => string.Equals(Status, "Active", System.StringComparison.Ordinal);

    /// <summary>
    /// Produces a new immutable Active version from this Draft (status=Active, ActiveFrom set).
    /// The store is responsible for the transactional guard (retire the previous Active,
    /// write the audit event). This method is a pure projection and performs no I/O.
    /// </summary>
    public SubjectVersion Activate(string activeFromUtc) => this with { Status = "Active", ActiveFrom = activeFromUtc };

    /// <summary>Produces a Retired projection of this version.</summary>
    public SubjectVersion Retire(string retiredAtUtc) => this with { Status = "Retired", RetiredAt = retiredAtUtc };

    /// <summary>Reads the declaration object contained in <see cref="Payload"/>.</summary>
    public SubjectDeclaration GetDeclaration() => SubjectDeclaration.FromPayload(Payload);
}
