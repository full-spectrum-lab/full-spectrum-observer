using System;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M3-FIX-04 — Unit coverage for the typed <see cref="UnknownStateContract"/>, the single
/// authoritative representation of the persisted <c>analysis_results.unknown_state</c> value.
/// The legal set is exactly UNKNOWN / KNOWN / PARTIAL (matching the DB CHECK constraint).
/// </summary>
public sealed class UnknownStateContractTests
{
    [Fact]
    public void UNKNOWN_is_a_valid_contract_value()
    {
        UnknownStateContract.IsValid(UnknownStateContract.Unknown).Should().BeTrue();
    }

    [Fact]
    public void KNOWN_is_a_valid_contract_value()
    {
        UnknownStateContract.IsValid(UnknownStateContract.Known).Should().BeTrue();
    }

    [Fact]
    public void PARTIAL_is_a_valid_contract_value()
    {
        UnknownStateContract.IsValid(UnknownStateContract.Partial).Should().BeTrue();
    }

    [Fact]
    public void RESOLVED_is_rejected_by_the_contract()
    {
        UnknownStateContract.IsValid("RESOLVED").Should().BeFalse();
        var act = () => UnknownStateContract.Parse("RESOLVED", FoundationReasonCodesProbe, "detail");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Null_empty_and_whitespace_are_rejected()
    {
        UnknownStateContract.IsValid(null).Should().BeFalse();
        UnknownStateContract.IsValid(string.Empty).Should().BeFalse();
        UnknownStateContract.IsValid("   ").Should().BeFalse();
    }

    [Fact]
    public void Arbitrary_unknown_string_is_rejected()
    {
        UnknownStateContract.IsValid("FOOBAR").Should().BeFalse();
        UnknownStateContract.IsValid("unknown").Should().BeFalse(); // case-sensitive
        UnknownStateContract.IsValid("Known").Should().BeFalse();  // case-sensitive
        var act = () => UnknownStateContract.ValidateOrThrow("MAYBE", "INVALID_UNKNOWN_STATE_CONTRACT");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FailClosed_default_is_UNKOWN_and_is_valid()
    {
        UnknownStateContract.FailClosed.Should().Be(UnknownStateContract.Unknown);
        UnknownStateContract.IsValid(UnknownStateContract.FailClosed).Should().BeTrue();
    }

    [Fact]
    public void ValidateOrThrow_returns_canonical_value_for_valid_input_and_throws_for_invalid()
    {
        UnknownStateContract.ValidateOrThrow(UnknownStateContract.Known, "INVALID_UNKNOWN_STATE_CONTRACT")
            .Should().Be(UnknownStateContract.Known);
        var act = () => UnknownStateContract.ValidateOrThrow("RESOLVED", "INVALID_UNKNOWN_STATE_CONTRACT");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*INVALID_UNKNOWN_STATE_CONTRACT*");
    }

    [Fact]
    public void FromVerbatimOrFailClosed_returns_UNKOWN_when_no_signal_is_present()
    {
        // The Engine v1.5.0 worker emits no explicit completeness signal today.
        UnknownStateContract.FromVerbatimOrFailClosed(null).Value.Should().Be(UnknownStateContract.Unknown);
        UnknownStateContract.FromVerbatimOrFailClosed("RESOLVED").Value.Should().Be(UnknownStateContract.Unknown);
    }

    [Fact]
    public void FromVerbatimOrFailClosed_honours_an_explicit_valid_signal()
    {
        UnknownStateContract.FromVerbatimOrFailClosed(UnknownStateContract.Known).Value.Should().Be(UnknownStateContract.Known);
        UnknownStateContract.FromVerbatimOrFailClosed(UnknownStateContract.Partial).Value.Should().Be(UnknownStateContract.Partial);
    }

    [Fact]
    public void Parse_round_trips_and_to_string_returns_the_canonical_value()
    {
        UnknownStateContract parsed = UnknownStateContract.Parse(UnknownStateContract.Partial, "X");
        parsed.Value.Should().Be(UnknownStateContract.Partial);
        parsed.ToString().Should().Be(UnknownStateContract.Partial);
        (parsed == UnknownStateContract.Parse(UnknownStateContract.Partial, "X")).Should().BeTrue();
    }

    private const string FoundationReasonCodesProbe = "INVALID_UNKNOWN_STATE_CONTRACT";
}
