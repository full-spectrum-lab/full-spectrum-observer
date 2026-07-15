namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// A knowledge source (knowledge base) base row. The stable identity is written once at
/// creation and never changes. Versioned content lives in <see cref="KnowledgeSourceVersion"/>.
/// </summary>
public sealed record KnowledgeSource
{
    /// <summary>Stable knowledge source identifier, e.g. <c>KS-2026-0011</c>.</summary>
    public required string SourceId { get; init; }

    /// <summary>Owning knowledge library identifier, e.g. <c>LIB-GLOBAL-001</c>.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Human-readable knowledge source name.</summary>
    public required string Name { get; init; }

    /// <summary>Creation timestamp (ISO-8601 UTC).</summary>
    public required string CreatedAt { get; init; }
}
