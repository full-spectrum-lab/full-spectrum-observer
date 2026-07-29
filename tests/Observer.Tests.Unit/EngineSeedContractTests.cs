using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M3-FIX-06 / SD-002 — Unit coverage for the typed <see cref="EngineSeedContract"/>, the single
/// authoritative representation of the engine seed. The seed is the first 8 hex characters of the
/// request content digest, interpreted as an UNSIGNED 32-bit value (UInt32 domain, 0..4294967295).
/// The legacy bug sign-truncated this to a negative <c>int</c> (e.g. 0xD783D8DF → -679225121), which
/// NumPy rejected as a random seed. These tests prove the seed is ALWAYS a non-negative UInt32 value.
/// </summary>
public sealed class EngineSeedContractTests
{
    // ---- FromContentDigest boundary cases (digest prefix -> seed) ----

    [Fact]
    public void FromContentDigest_all_zero_prefix_yields_zero()
    {
        EngineSeedContract.FromContentDigest("00000000").Should().Be(0L);
    }

    [Fact]
    public void FromContentDigest_one_prefix_yields_one()
    {
        EngineSeedContract.FromContentDigest("00000001").Should().Be(1L);
    }

    [Fact]
    public void FromContentDigest_int32_max_prefix_yields_int32_max()
    {
        EngineSeedContract.FromContentDigest("7fffffff").Should().Be(2147483647L);
    }

    [Fact]
    public void FromContentDigest_int32_max_plus_one_prefix_yields_2147483648()
    {
        // The critical boundary: the legacy int cast would have produced a NEGATIVE value here.
        EngineSeedContract.FromContentDigest("80000000").Should().Be(2147483648L);
    }

    [Fact]
    public void FromContentDigest_real_failure_input_yields_positive_uint32()
    {
        // The exact digest prefix that triggered the original INCIDENT (0xD783D8DF).
        EngineSeedContract.FromContentDigest("d783d8df").Should().Be(3615742175L);
    }

    [Fact]
    public void FromContentDigest_uint32_max_prefix_yields_uint32_max()
    {
        EngineSeedContract.FromContentDigest("ffffffff").Should().Be(4294967295L);
    }

    // ---- Determinism ----

    [Fact]
    public void FromContentDigest_is_deterministic_across_100_calls()
    {
        const string digest = "d783d8dfabcdef0123456789";
        long first = EngineSeedContract.FromContentDigest(digest);
        for (int i = 0; i < 100; i++)
        {
            EngineSeedContract.FromContentDigest(digest).Should().Be(first);
        }
    }

    // ---- Case insensitivity ----

    [Fact]
    public void FromContentDigest_is_case_insensitive()
    {
        EngineSeedContract.FromContentDigest("D783D8DF").Should().Be(3615742175L);
        EngineSeedContract.FromContentDigest("d783d8df").Should().Be(EngineSeedContract.FromContentDigest("D783D8DF"));
    }

    // ---- Illegal inputs are rejected ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("zzzzzzzz")]
    [InlineData("1234567")]
    public void FromContentDigest_rejects_illegal_input(string? digest)
    {
        Action act = () => EngineSeedContract.FromContentDigest(digest!);
        act.Should().Throw<Exception>();
    }

    // ---- IsValid domain checks ----

    [Fact]
    public void IsValid_accepts_domain_boundaries()
    {
        EngineSeedContract.IsValid(0L).Should().BeTrue();
        EngineSeedContract.IsValid(4294967295L).Should().BeTrue();
    }

    [Fact]
    public void IsValid_rejects_out_of_domain()
    {
        EngineSeedContract.IsValid(-1L).Should().BeFalse();
        EngineSeedContract.IsValid(4294967296L).Should().BeFalse();
        EngineSeedContract.IsValid(long.MaxValue).Should().BeFalse();
    }

    [Fact]
    public void IsValid_negative_seeds_are_impossible()
    {
        EngineSeedContract.IsValid(-1L).Should().BeFalse();
        EngineSeedContract.IsValid(long.MinValue).Should().BeFalse();
    }

    // ---- ValidateOrThrow ----

    [Fact]
    public void ValidateOrThrow_does_not_throw_for_valid_seed()
    {
        Action act = () => EngineSeedContract.ValidateOrThrow(3615742175L);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(4294967296L)]
    public void ValidateOrThrow_throws_for_invalid_seed(long seed)
    {
        Action act = () => EngineSeedContract.ValidateOrThrow(seed);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*INVALID_ENGINE_SEED_CONTRACT*");
    }

    // ---- JSON round trip: long fully carries the UInt32 domain (no negative overflow) ----

    [Fact]
    public void EngineFacadeRequest_seed_round_trips_for_uint32_max()
    {
        EngineFacadeRequest request = BuildRequestWithSeed(4294967295L);
        string json = JsonSerializer.Serialize(request);
        EngineFacadeRequest? round = JsonSerializer.Deserialize<EngineFacadeRequest>(json);
        round.Should().NotBeNull();
        round!.Seed.Should().Be(4294967295L);
        round.Seed.Should().NotBe(-1L);
    }

    [Fact]
    public void EngineFacadeRequest_seed_round_trips_for_int32_max_plus_one()
    {
        EngineFacadeRequest request = BuildRequestWithSeed(2147483648L);
        string json = JsonSerializer.Serialize(request);
        EngineFacadeRequest? round = JsonSerializer.Deserialize<EngineFacadeRequest>(json);
        round.Should().NotBeNull();
        round!.Seed.Should().Be(2147483648L);
        round.Seed.Should().NotBe(-2147483648L);
    }

    // ---- Batch property test: 10000 random digests always yield a valid, non-negative, stable seed ----

    [Fact]
    public void FromContentDigest_over_random_digests_is_in_domain_non_negative_and_stable()
    {
        using var rng = RandomNumberGenerator.Create();
        for (int i = 0; i < 10000; i++)
        {
            byte[] bytes = new byte[32];
            rng.GetBytes(bytes);
            byte[] hash = SHA256.HashData(bytes);
            string digest = Convert.ToHexStringLower(hash); // 64 hex chars
            string prefix = digest.Substring(0, 8);

            long seed = EngineSeedContract.FromContentDigest(prefix);
            seed.Should().BeGreaterThanOrEqualTo(0L);
            seed.Should().BeLessThanOrEqualTo(4294967295L);
            // Stability: same input -> same seed.
            EngineSeedContract.FromContentDigest(prefix).Should().Be(seed);
            EngineSeedContract.IsValid(seed).Should().BeTrue();
        }
    }

    // ---- Helpers ----

    private static EngineFacadeRequest BuildRequestWithSeed(long seed)
    {
        JsonElement scenario = JsonSerializer.SerializeToElement(new { placeholder = "scenario" });
        JsonElement engine = JsonSerializer.SerializeToElement(new { placeholder = "engine" });
        return new EngineFacadeRequest
        {
            Protocol = "fs-observer-engine-facade/1",
            RequestId = "req-seed-roundtrip",
            Operation = "evaluate",
            Engine = engine,
            Seed = seed,
            FixedTimeUtc = "2026-07-04T00:00:00Z",
            Scenario = scenario,
            OutputSerialization = "FSE-PYJSON-1",
        };
    }
}
