using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M2 P1 main line — idempotency decision for the analysis lifecycle, keyed by JobId (the
/// analysis_tasks primary key) plus the request fingerprint (the stored content digest).
/// A repeated submission of the same JobId produces no new side effect on the store.
/// </summary>
public sealed class JobIdempotencyTests
{
    [Fact]
    public void Decide_is_miss_when_no_existing_task_for_the_job_id()
    {
        JobIdempotency.Decide(existingContentDigest: null, requestedContentDigest: "sha-abc")
            .Should().Be(JobIdempotency.Outcome.Miss);
    }

    [Fact]
    public void Decide_is_hit_when_the_stored_fingerprint_matches()
    {
        JobIdempotency.Decide(existingContentDigest: "sha-abc", requestedContentDigest: "sha-abc")
            .Should().Be(JobIdempotency.Outcome.Hit);
    }

    [Fact]
    public void Decide_is_conflict_when_the_stored_fingerprint_differs()
    {
        JobIdempotency.Decide(existingContentDigest: "sha-abc", requestedContentDigest: "sha-xyz")
            .Should().Be(JobIdempotency.Outcome.Conflict);
    }

    [Fact]
    public void Decide_is_case_sensitive_on_the_fingerprint()
    {
        JobIdempotency.Decide(existingContentDigest: "sha-ABC", requestedContentDigest: "sha-abc")
            .Should().Be(JobIdempotency.Outcome.Conflict);
    }

    [Fact]
    public void Decide_throws_when_the_requested_fingerprint_is_null()
    {
        var act = () => JobIdempotency.Decide(existingContentDigest: "sha-abc", requestedContentDigest: null!);
        act.Should().Throw<System.ArgumentNullException>();
    }
}
