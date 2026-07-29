using System;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Pure idempotency decision for the analysis task lifecycle (P1 main line). The idempotency key is
/// bound to the <c>JobId</c> (the <c>analysis_tasks</c> primary key) PLUS the request fingerprint
/// (the stored content digest). A repeat submission that carries the same JobId is resolved here,
/// WITHOUT creating any new side effect on the store.
///
/// <list type="bullet">
///   <item><description><see cref="Outcome.Miss"/> — no task exists for the JobId; the caller may create one.</description></item>
///   <item><description><see cref="Outcome.Hit"/> — a task exists AND its stored fingerprint equals the request fingerprint; the caller must return the existing task (idempotent, no new work).</description></item>
///   <item><description><see cref="Outcome.Conflict"/> — a task exists BUT the fingerprint differs; the caller must reject the duplicate submission.</description></item>
/// </list>
/// </summary>
public static class JobIdempotency
{
    /// <summary>The resolved idempotency outcome for an incoming request.</summary>
    public enum Outcome
    {
        /// <summary>No existing task for the JobId; safe to create.</summary>
        Miss,

        /// <summary>Existing task with a matching request fingerprint; return it (no new side effects).</summary>
        Hit,

        /// <summary>Existing task with a conflicting request fingerprint; reject the duplicate.</summary>
        Conflict,
    }

    /// <summary>
    /// Decides how to treat an incoming request against the stored task for the same JobId.
    /// </summary>
    /// <param name="existingContentDigest">The stored content digest of the existing task, or
    /// <c>null</c> when no task exists for the JobId.</param>
    /// <param name="requestedContentDigest">The request fingerprint (content digest) of the incoming
    /// request. Never <c>null</c>.</param>
    /// <returns>The resolved <see cref="Outcome"/>.</returns>
    public static Outcome Decide(string? existingContentDigest, string requestedContentDigest)
    {
        ArgumentNullException.ThrowIfNull(requestedContentDigest);
        if (existingContentDigest is null)
        {
            return Outcome.Miss;
        }
        return string.Equals(existingContentDigest, requestedContentDigest, StringComparison.Ordinal)
            ? Outcome.Hit
            : Outcome.Conflict;
    }
}
