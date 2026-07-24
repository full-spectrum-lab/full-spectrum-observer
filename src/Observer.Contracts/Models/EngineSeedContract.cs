using System;
using System.Globalization;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// M3-FIX-06 / SD-002 — typed seed contract. The engine seed is the first 8 hex characters of the
/// request content digest, interpreted as an unsigned 32-bit value (UInt32 domain, 0..4294967295).
/// The Facade MUST only ever emit a non-negative seed within this domain; a negative or out-of-range
/// seed must be rejected before the Python Worker is spawned (never reaching ENGINE_SIMULATION_ERROR).
/// </summary>
public static class EngineSeedContract
{
    public const long MinValue = 0L;
    public const long MaxValue = 4294967295L; // uint.MaxValue
    public const string ReasonCode = "INVALID_ENGINE_SEED_CONTRACT";

    public static long FromContentDigest(string contentDigest)
    {
        if (string.IsNullOrWhiteSpace(contentDigest) || contentDigest.Length < 8)
        {
            throw new ArgumentException(
                $"Content digest must be at least 8 hex characters; got: {contentDigest ?? "null"}.",
                nameof(contentDigest));
        }

        // Parse the first 8 hex characters as an UNSIGNED 32-bit value. uint.Parse yields a positive
        // value in [0, 4294967295]; returning it as long preserves the full UInt32 domain with no
        // sign truncation.
        uint value = uint.Parse(
            contentDigest.AsSpan(0, 8),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        return value; // uint -> long, always non-negative, full UInt32 range
    }

    public static bool IsValid(long seed) => seed >= MinValue && seed <= MaxValue;

    public static void ValidateOrThrow(long seed)
    {
        if (!IsValid(seed))
        {
            throw new InvalidOperationException(
                $"[{ReasonCode}] Engine seed {seed} is outside the UInt32 domain [0, 4294967295].");
        }
    }
}
