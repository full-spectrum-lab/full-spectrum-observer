using System;
using System.Collections.Generic;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Single, typed, verifiable contract for the Engine version persisted in
/// <c>runtime_snapshots.engine_version</c> (and any other store column carrying the Engine
/// identity). The canonical, frozen value is <c>"v1.5.0"</c>, equal to
/// <c>EngineV15Contract.EngineTag</c> (the frozen Engine v1.5.0 identity — NEVER renegotiated).
///
/// <para>Root cause (SD-001 / M3-FIX-05): the DB CHECK constraint for
/// <c>runtime_snapshots.engine_version</c> was mistakenly pinned to the legacy, wire-only form
/// <c>'1.5.0'</c> (no "v" prefix), while the frozen Engine identity — and therefore every value the
/// Engine writes and the store must accept — is <c>'v1.5.0'</c>. The mismatch surfaced as a SQLite
/// CHECK failure on <c>InsertRuntimeSnapshotAsync</c>, driving the analysis to
/// COMMIT_FAILED → RECOVERY_REQUIRED.</para>
///
/// <para>This type is the ONLY legitimate source of the Engine version. The DB CHECK is
/// defence-in-depth; this contract is the authoritative, fail-closed gate (see
/// <c>AnalysisWorkspace</c>). The normalization rules are EXPLICIT and NON-GUESSING:
/// <list type="bullet">
///   <item><description><c>"v1.5.0"</c> → <c>"v1.5.0"</c> (canonical, exactly as written by the Engine).</description></item>
///   <item><description><c>"1.5.0"</c> → <c>"v1.5.0"</c> (legacy wire form, explicitly canonicalized — never invented).</description></item>
///   <item><description>Any other value → explicitly REJECTED (no fabricated conversion).</description></item>
/// </list>
/// No bare <c>"v1.5.0"</c> / <c>"1.5.0"</c> literal may be scattered through the DB / Web / Store /
/// test code; always route through this contract's <see cref="IsSupported"/> /
/// <see cref="ValidateOrThrow"/> / <see cref="NormalizeLegacy"/>.</para>
/// </summary>
public static class EngineVersionContract
{
    /// <summary>
    /// The canonical, frozen Engine version. MUST equal <c>EngineV15Contract.EngineTag</c>
    /// ("v1.5.0"). Defined as a literal here (rather than a cross-project <c>const</c> reference)
    /// to avoid a Contracts ↔ EngineFacade dependency cycle; an equality-lock test asserts the two
    /// never diverge.
    /// </summary>
    public const string CanonicalVersion = "v1.5.0";

    /// <summary>
    /// Migration identifier for the canonicalization rebuild. Single source of truth shared with the
    /// Store migration runner (<c>EngineVersionCanonicalizationMigration.MigrationId</c>).
    /// </summary>
    public const string MigrationId = "MIG-OBS-V03-ENGINE-VERSION-CANONICALIZATION";

    /// <summary>
    /// The explicit, non-guessing canonicalization map. A value absent here is REJECTED — we never
    /// infer a conversion for an unrecognized Engine version.
    /// </summary>
    private static readonly Dictionary<string, string> CanonicalMap = new(StringComparer.Ordinal)
    {
        ["v1.5.0"] = "v1.5.0",
        ["1.5.0"] = "v1.5.0",
    };

    /// <summary>
    /// Returns true when <paramref name="value"/> is a recognized Engine version form — the canonical
    /// <see cref="CanonicalVersion"/> or the legacy wire form <c>"1.5.0"</c> (case-sensitive).
    /// </summary>
    public static bool IsSupported(string? value) =>
        value is not null && CanonicalMap.ContainsKey(value);

    /// <summary>
    /// Normalizes a recognized Engine version to its canonical form. Throws
    /// <see cref="InvalidOperationException"/> for any unrecognized value (no fabricated conversion).
    /// <list type="bullet">
    ///   <item><description><c>"v1.5.0"</c> → <c>"v1.5.0"</c></description></item>
    ///   <item><description><c>"1.5.0"</c> → <c>"v1.5.0"</c></description></item>
    ///   <item><description>anything else → throws</description></item>
    /// </list>
    /// </summary>
    public static string NormalizeLegacy(string? value)
    {
        if (value is null || !CanonicalMap.TryGetValue(value, out string? canonical))
        {
            throw new InvalidOperationException(
                $"Unsupported Engine version '{value ?? "null"}'. The only supported forms are " +
                $"'{CanonicalVersion}' (canonical) and the legacy wire form '1.5.0'; no conversion is inferred for any other value.");
        }
        return canonical;
    }

    /// <summary>
    /// Validates a candidate Engine version, throwing <see cref="InvalidOperationException"/>
    /// (reason <paramref name="reasonCode"/>) on any non-conforming value, and returning the
    /// canonical form so callers can persist it directly.
    /// </summary>
    public static string ValidateOrThrow(string? value, string reasonCode, string? detail = null)
    {
        if (!IsSupported(value))
        {
            throw new InvalidOperationException(
                detail ?? $"{reasonCode}: engine_version '{value ?? "null"}' is not a supported canonical Engine version " +
                $"(expected '{CanonicalVersion}'; legacy '1.5.0' also accepted).");
        }
        return CanonicalMap[value!];
    }
}
