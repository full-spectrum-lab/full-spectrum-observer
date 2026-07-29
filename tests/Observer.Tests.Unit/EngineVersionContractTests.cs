using System;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M3-FIX-05 / SD-001 — Unit coverage for the typed <see cref="EngineVersionContract"/>, the single
/// authoritative representation of the persisted <c>runtime_snapshots.engine_version</c> value. The
/// canonical, frozen form is "v1.5.0" (equal to EngineV15Contract.EngineTag). The legacy wire form
/// "1.5.0" is explicitly canonicalized; every other value is rejected (no fabricated conversion).
/// </summary>
public sealed class EngineVersionContractTests
{
    [Fact]
    public void CanonicalVersion_is_v1_5_0()
    {
        EngineVersionContract.CanonicalVersion.Should().Be("v1.5.0");
    }

    [Fact]
    public void CanonicalVersion_matches_EngineV15Contract_EngineTag()
    {
        // Single-source-of-truth lock: the contract MUST equal the frozen Engine identity.
        EngineVersionContract.CanonicalVersion.Should().Be("v1.5.0");
        EngineVersionContract.CanonicalVersion.Should().Be(EngineV15Contract.EngineTag);
    }

    [Fact]
    public void MigrationId_matches_the_required_migration_identifier()
    {
        EngineVersionContract.MigrationId.Should().Be("MIG-OBS-V03-ENGINE-VERSION-CANONICALIZATION");
    }

    [Fact]
    public void IsSupported_true_for_canonical_and_legacy_forms()
    {
        EngineVersionContract.IsSupported(EngineVersionContract.CanonicalVersion).Should().BeTrue();
        EngineVersionContract.IsSupported("1.5.0").Should().BeTrue();
    }

    [Fact]
    public void IsSupported_false_for_illegal_and_null()
    {
        EngineVersionContract.IsSupported("2.0.0").Should().BeFalse();
        EngineVersionContract.IsSupported("v1.4.0").Should().BeFalse();
        EngineVersionContract.IsSupported("V1.5.0").Should().BeFalse(); // case-sensitive
        EngineVersionContract.IsSupported("garbage").Should().BeFalse();
        EngineVersionContract.IsSupported(null).Should().BeFalse();
        EngineVersionContract.IsSupported(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void NormalizeLegacy_canonical_is_idempotent()
    {
        EngineVersionContract.NormalizeLegacy("v1.5.0").Should().Be("v1.5.0");
    }

    [Fact]
    public void NormalizeLegacy_legacy_is_canonicalized()
    {
        EngineVersionContract.NormalizeLegacy("1.5.0").Should().Be("v1.5.0");
    }

    [Fact]
    public void NormalizeLegacy_rejects_unrecognized_values()
    {
        var act = () => EngineVersionContract.NormalizeLegacy("2.0.0");
        act.Should().Throw<InvalidOperationException>();

        var nullAct = () => EngineVersionContract.NormalizeLegacy(null);
        nullAct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateOrThrow_returns_canonical_for_valid_value()
    {
        EngineVersionContract.ValidateOrThrow("v1.5.0", "INVALID_ENGINE_VERSION_CONTRACT").Should().Be("v1.5.0");
        EngineVersionContract.ValidateOrThrow("1.5.0", "INVALID_ENGINE_VERSION_CONTRACT").Should().Be("v1.5.0");
    }

    [Fact]
    public void ValidateOrThrow_throws_with_reason_code_for_invalid_value()
    {
        var act = () => EngineVersionContract.ValidateOrThrow("9.9.9", "INVALID_ENGINE_VERSION_CONTRACT");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*INVALID_ENGINE_VERSION_CONTRACT*");
    }

    [Fact]
    public void ValidateOrThrow_throws_on_null()
    {
        var act = () => EngineVersionContract.ValidateOrThrow(null, "INVALID_ENGINE_VERSION_CONTRACT");
        act.Should().Throw<InvalidOperationException>();
    }
}
