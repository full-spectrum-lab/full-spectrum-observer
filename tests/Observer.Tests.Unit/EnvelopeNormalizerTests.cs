using System;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// DET-001-FIX — unit coverage for <see cref="EnvelopeNormalizer"/>, the single fail-closed
/// chokepoint that guarantees every scenario sent to the pinned Engine v1.5.0 worker carries a
/// deterministic <c>simulation_id</c>. Both the Web path (<see cref="EngineFacade"/>) and the CLI
/// path (FoundationAnalysisUseCase) call this same method, so these tests cover both envelopes.
///
/// Frozen rule (OWNER authorization DET-001-FIX):
///   - existing non-empty simulation_id -&gt; kept verbatim (never overwritten / re-derived);
///   - missing simulation_id            -&gt; derived as "SIM-" + first 16 hex chars of the content
///                                         digest (lowercase, canonical);
///   - invalid / empty / &lt; 16-hex digest -&gt; rejected BEFORE the worker is spawned (never time /
///                                         GUID / random / process id / machine name / task time).
/// </summary>
public sealed class EnvelopeNormalizerTests
{
    private const string FrozenEngineCommit = "88493007d4e00344c70a70ed0e5a5d652dec86f5";
    private const string FrozenEngineTag = "v1.5.0";

    private static JsonElement Scenario(object model) => JsonSerializer.SerializeToElement(model);

    // 1. An existing simulation_id is preserved VERBATIM (never overwritten or re-derived).
    [Fact]
    public void PreservesExistingSimulationId_Verbatim()
    {
        JsonElement scenario = Scenario(new
        {
            user_question = "q",
            ai_output = "a",
            context = "c",
            simulation_id = "SIM-EXISTING-XYZ",
        });

        (JsonElement normalized, string resolved) =
            EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe");

        resolved.Should().Be("SIM-EXISTING-XYZ");
        normalized.GetProperty("simulation_id").GetString().Should().Be("SIM-EXISTING-XYZ");
        // Unrelated fields must survive untouched.
        normalized.GetProperty("user_question").GetString().Should().Be("q");
        normalized.GetProperty("ai_output").GetString().Should().Be("a");
        normalized.GetProperty("context").GetString().Should().Be("c");
    }

    // 2. When simulation_id is missing it is derived as SIM-<first 16 hex of digest, lowercase>.
    [Fact]
    public void DerivesSimulationId_WhenMissing()
    {
        JsonElement scenario = Scenario(new { user_question = "q", ai_output = "a", context = "c" });

        (JsonElement normalized, string resolved) =
            EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe");

        resolved.Should().Be("SIM-deadbeefcafebabe");
        normalized.GetProperty("simulation_id").GetString().Should().Be("SIM-deadbeefcafebabe");
    }

    // 3. Same content digest -> same resolved simulation_id (determinism, the core guarantee).
    [Fact]
    public void SameDigest_ProducesSameSimulationId()
    {
        JsonElement scenario = Scenario(new { user_question = "q" });
        string first = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe").ResolvedSimulationId;
        string second = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe").ResolvedSimulationId;

        first.Should().Be(second).And.Be("SIM-deadbeefcafebabe");
    }

    // 4. Different content digest -> different resolved simulation_id.
    [Fact]
    public void DifferentDigest_ProducesDifferentSimulationId()
    {
        JsonElement scenario = Scenario(new { user_question = "q" });
        string a = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbb").ResolvedSimulationId;
        string b = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "bbbbbbbbbbbbbbbbcccccccccccccccc").ResolvedSimulationId;

        a.Should().NotBe(b);
        a.Should().Be("SIM-aaaaaaaaaaaaaaaa");
        b.Should().Be("SIM-bbbbbbbbbbbbbbbb");
    }

    // 5. The derived simulation_id is canonical / case-insensitive with respect to the digest.
    [Fact]
    public void DerivedSimulationId_IsCaseInsensitiveToDigest()
    {
        JsonElement scenario = Scenario(new { user_question = "q" });
        string upper = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "ABCDEF0123456789DEADBEEFCAFEBABE").ResolvedSimulationId;
        string lower = EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "abcdef0123456789deadbeefcafebabe").ResolvedSimulationId;

        upper.Should().Be(lower).And.Be("SIM-abcdef0123456789");
    }

    // 6. Invalid / empty / non-hex / <16-hex content digest is rejected BEFORE the worker is spawned.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("zzzzzzzzzzzzzzzz")]
    [InlineData("1234567890abcde")]
    public void RejectsInvalidContentDigest(string? digest)
    {
        JsonElement scenario = Scenario(new { user_question = "q" });
        Action act = () => EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, digest!);

        act.Should().Throw<ContractViolationException>()
            .WithMessage($"*[{EnvelopeNormalizer.ReasonCodeInvalidContentDigest}]*");
    }

    // 7. The Web FORM envelope builder embeds the resolved simulation_id into the worker request.
    [Fact]
    public void WebEnvelope_RetainsSimulationId()
    {
        JsonElement scenario = Scenario(new { user_question = "q", ai_output = "a", context = "c" });
        (JsonElement normalized, string resolved) =
            EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe");

        var request = new EngineFacadeRequest
        {
            Protocol = "fs-observer-engine-facade/1",
            RequestId = "req-web-retain",
            Operation = "evaluate",
            Engine = JsonSerializer.SerializeToElement(new { version = FrozenEngineTag, commit = FrozenEngineCommit }),
            Seed = 0L,
            FixedTimeUtc = "2026-07-04T00:00:00Z",
            Scenario = normalized,
            OutputSerialization = "FSE-PYJSON-1",
        };

        string json = JsonSerializer.Serialize(request);
        using JsonDocument document = JsonDocument.Parse(json);
        string? embedded = document.RootElement.GetProperty("scenario").GetProperty("simulation_id").GetString();

        embedded.Should().Be(resolved).And.Be("SIM-deadbeefcafebabe");
    }

    // 8. A CLI explicit (caller-supplied) simulation_id is NOT overwritten by derivation.
    [Fact]
    public void CliExplicitSimulationId_NotOverwritten()
    {
        // Mimics the CLI canonical context carrying an explicit caller-supplied simulation_id.
        JsonElement scenario = Scenario(new
        {
            user_question = "q",
            ai_output = "a",
            context = "c",
            simulation_id = "SIM-CLI-EXPLICIT",
        });

        (JsonElement normalized, string resolved) =
            EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe");

        resolved.Should().Be("SIM-CLI-EXPLICIT");
        normalized.GetProperty("simulation_id").GetString().Should().Be("SIM-CLI-EXPLICIT");
    }

    // 9. The serialized (normalized) envelope retains the simulation_id across a JSON round-trip.
    [Fact]
    public void SerializedEnvelope_RetainsSimulationId()
    {
        JsonElement scenario = Scenario(new { user_question = "q" });
        (JsonElement normalized, string resolved) =
            EnvelopeNormalizer.EnsureDeterministicSimulationId(scenario, "deadbeefcafebabedeadbeefcafebabe");

        string json = normalized.GetRawText();
        JsonElement round = JsonSerializer.Deserialize<JsonElement>(json);

        round.GetProperty("simulation_id").GetString().Should().Be(resolved).And.Be("SIM-deadbeefcafebabe");
    }

    // 10. The fix does NOT touch the pinned Engine v1.5.0 identity (engine / commit unchanged).
    [Fact]
    public void PinnedEngineIdentity_Unchanged()
    {
        EngineV15Contract.EngineTag.Should().Be(FrozenEngineTag);
        EngineV15Contract.EngineCommit.Should().Be(FrozenEngineCommit);
    }
}
