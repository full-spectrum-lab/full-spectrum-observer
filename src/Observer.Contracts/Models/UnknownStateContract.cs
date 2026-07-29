using System;
using System.Collections.Generic;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Single, typed, verifiable contract for the persisted <c>analysis_results.unknown_state</c>
/// column. The canonical DB CHECK constraint (see
/// <c>Observer.Store/Data/Migrations/Init.sql</c>:
/// <c>unknown_state IN ('UNKNOWN','KNOWN','PARTIAL')</c>) is the LAST line of defence; this type is
/// the authoritative in-memory representation and the ONLY legitimate source for that column.
///
/// <para>Root cause (M3-FIX-04): the Engine v1.5.0 worker does NOT emit an explicit unknown-state
/// completeness signal, yet the legacy EngineFacade (FullSpectrum.Observer.EngineFacade)
/// hardcoded <c>"RESOLVED"</c> — a value the DB CHECK rejects. The first
/// <c>analysis_results</c> INSERT therefore failed, surfacing as COMMIT_FAILED → RECOVERY_REQUIRED.
/// The fix is two-fold and fail-closed:
/// <list type="bullet">
///   <item><description>The EngineFacade emits a contract-valid value (defaulting to
///     <see cref="FailClosed"/> = UNKNOWN at the ADAPTER_POLICY layer) because context completeness
///     is not provably full.</description></item>
///   <item><description>The orchestrator re-validates here BEFORE the R1-D commit chain, so any
///     non-conforming value is rejected at OUTPUT_VALIDATION (reason
///     <c>INVALID_UNKNOWN_STATE_CONTRACT</c>) and is never written to the store.</description></item>
/// </list>
/// </para>
///
/// <para>The three legal values are the ONLY ones. A bare string MUST NOT be scattered through the
/// code; always route through this contract's <see cref="IsValid"/> / <see cref="Parse"/> /
/// <see cref="ValidateOrThrow"/>.</para>
/// </summary>
public readonly struct UnknownStateContract : IEquatable<UnknownStateContract>
{
    /// <summary>The analysis context is not fully known (default / fail-closed value).</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>The analysis context is fully known (requires an explicit, provable completeness signal).</summary>
    public const string Known = "KNOWN";

    /// <summary>The analysis context is partially known.</summary>
    public const string Partial = "PARTIAL";

    /// <summary>
    /// Fail-closed default used when the Engine/Adapter provides no explicit completeness signal.
    /// Context completeness is never provably full from a Worker SUCCESS alone, so we default to
    /// <see cref="Unknown"/> rather than <see cref="Known"/>.
    /// </summary>
    public const string FailClosed = Unknown;

    private static readonly HashSet<string> ValidValues = new(StringComparer.Ordinal)
    {
        Unknown,
        Known,
        Partial,
    };

    /// <summary>Gets the canonical string value carried by this instance.</summary>
    public string Value { get; }

    private UnknownStateContract(string value) => Value = value;

    /// <summary>
    /// Returns true when <paramref name="value"/> is exactly one of
    /// <see cref="Unknown"/> / <see cref="Known"/> / <see cref="Partial"/> (case-sensitive).
    /// </summary>
    public static bool IsValid(string? value) =>
        value is not null && ValidValues.Contains(value);

    /// <summary>
    /// Parses a candidate string into a contract value. Throws <see cref="InvalidOperationException"/>
    /// carrying the <paramref name="reasonCode"/> when the value is not a legal unknown-state.
    /// </summary>
    public static UnknownStateContract Parse(string? value, string reasonCode, string? detail = null)
    {
        if (!IsValid(value))
        {
            throw new InvalidOperationException(
                detail ?? $"{reasonCode}: unknown_state '{value ?? "null"}' is not a legal value (expected UNKNOWN / KNOWN / PARTIAL).");
        }
        return new UnknownStateContract(value!);
    }

    /// <summary>
    /// Validates a candidate unknown-state string, throwing <see cref="InvalidOperationException"/>
    /// (reason <paramref name="reasonCode"/>) on any non-conforming value. Returns the validated
    /// canonical value so callers can use the result directly.
    /// </summary>
    public static string ValidateOrThrow(string? value, string reasonCode, string? detail = null)
    {
        UnknownStateContract parsed = Parse(value, reasonCode, detail);
        return parsed.Value;
    }

    /// <summary>
    /// Honours an explicit verbatim completeness signal from the Engine/Adapter when it is already a
    /// legal value; otherwise fails closed to <see cref="FailClosed"/> (UNKNOWN). This is the mapping
    /// entry point for the EngineFacade: today the Engine emits no signal, so it returns UNKNOWN; a
    /// future Engine/Adapter that supplies an explicit KNOWN/PARTIAL signal would be honoured here.
    /// </summary>
    public static UnknownStateContract FromVerbatimOrFailClosed(string? verbatimSignal) =>
        IsValid(verbatimSignal) ? new UnknownStateContract(verbatimSignal!) : new UnknownStateContract(FailClosed);

    /// <summary>Returns the contract value for a legal string, or <see langword="null"/> when invalid (non-throwing).</summary>
    public static UnknownStateContract? TryParse(string? value) =>
        IsValid(value) ? new UnknownStateContract(value!) : null;

    /// <inheritdoc />
    public bool Equals(UnknownStateContract other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UnknownStateContract other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(UnknownStateContract left, UnknownStateContract right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(UnknownStateContract left, UnknownStateContract right) => !left.Equals(right);
}
