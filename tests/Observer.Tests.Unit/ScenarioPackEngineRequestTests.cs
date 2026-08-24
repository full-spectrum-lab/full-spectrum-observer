using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class ScenarioPackEngineRequestTests
{
    [Fact]
    public void Two_packs_use_same_factory_and_produce_distinct_digest_bound_requests()
    {
        LoadedScenarioPack knowledge = Load("knowledge-conflict");
        LoadedScenarioPack policy = Load("synthetic-policy-conflict");
        var factory = new ScenarioPackEngineRequestFactory();

        ScenarioPackEngineRequest first = factory.BuildGoldenRequest(knowledge, "KC_GOLDEN_001");
        ScenarioPackEngineRequest second = factory.BuildGoldenRequest(policy, "SPC_GOLDEN_001");

        first.Request.EngineVersion.Should().Be("v1.5.0");
        second.Request.EngineVersion.Should().Be("v1.5.0");
        first.Request.ProfileVersion.Should().Be("1.5.0", "the frozen Engine Profile is distinct from the Pack policy Profile");
        first.Profiles.Should().ContainSingle().Which.Should().Be(new ScenarioPackProfileIdentity(
            "profiles/default.profile.json", "kc.default", "1.0.0",
            first.Profiles.Single().Digest));
        first.Profiles.Single().Digest.Should().MatchRegex("^[0-9a-f]{64}$");
        first.InputDigest.Should().HaveLength(64).And.NotBe(second.InputDigest);
        first.Request.Input.CanonicalInput.GetProperty("scenario_pack").GetProperty("digest").GetString()
            .Should().Be(knowledge.Identity.Digest);
        second.Request.Input.CanonicalInput.GetProperty("scenario_pack").GetProperty("digest").GetString()
            .Should().Be(policy.Identity.Digest);
        first.Request.Input.CanonicalInput.GetProperty("pack_input").GetProperty("sources").GetArrayLength()
            .Should().Be(2);
        second.Request.Input.CanonicalInput.GetProperty("pack_input").GetProperty("clauses").GetArrayLength()
            .Should().Be(2);
    }

    [Fact]
    public void Request_generation_is_deterministic_for_same_frozen_pack_and_case()
    {
        LoadedScenarioPack pack = Load("knowledge-conflict");
        var factory = new ScenarioPackEngineRequestFactory();

        ScenarioPackEngineRequest left = factory.BuildGoldenRequest(pack, "KC_GOLDEN_001");
        ScenarioPackEngineRequest right = factory.BuildGoldenRequest(pack, "KC_GOLDEN_001");

        right.InputDigest.Should().Be(left.InputDigest);
        right.Request.Input.CanonicalInput.GetRawText().Should().Be(left.Request.Input.CanonicalInput.GetRawText());
        right.Request.Subject.Declaration.GetRawText().Should().Be(left.Request.Subject.Declaration.GetRawText());
        ScenarioPackEngineRequestFactory.CreateProfileBindingJson(right)
            .Should().Be(ScenarioPackEngineRequestFactory.CreateProfileBindingJson(left));
        right.Request.Input.CanonicalInput.GetProperty("policy_profiles")[0].GetProperty("digest").GetString()
            .Should().Be(left.Profiles.Single().Digest);
    }

    [Fact]
    public void Undeclared_golden_case_is_rejected()
    {
        LoadedScenarioPack pack = Load("knowledge-conflict");
        var factory = new ScenarioPackEngineRequestFactory();

        Action build = () => factory.BuildGoldenRequest(pack, "UNKNOWN_CASE");

        build.Should().Throw<ScenarioPackLoadException>()
            .Where(error => error.ReasonCode == ScenarioPackReasonCodes.ReferenceMissing);
    }

    [Fact]
    public void Candidate_projection_rejects_an_Engine_hard_gate()
    {
        LoadedScenarioPack pack = Load("knowledge-conflict");
        ScenarioPackEngineRequest request = new ScenarioPackEngineRequestFactory()
            .BuildGoldenRequest(pack, "KC_GOLDEN_001");
        var response = new EngineResponse
        {
            EngineVersion = request.Request.EngineVersion,
            EngineCommit = request.Request.EngineCommit,
            SchemaVersion = request.Request.SchemaVersion,
            SchemaDigest = request.Request.SchemaDigest,
            AnalyzerVersion = request.Request.AnalyzerVersion,
            ProfileVersion = request.Request.ProfileVersion,
            UnknownState = "UNKNOWN",
            HardGate = true,
            RuntimeDigest = new string('a', 64),
        };

        Action project = () => ScenarioPackEngineResultProjector.Project(
            "TASK-1", "RESULT-1", request, response, "2026-08-25T00:00:00Z");

        project.Should().Throw<InvalidDataException>().WithMessage("*SCENARIO_PACK_AUTOMATIC_ACTION_FORBIDDEN*");
    }

    private static LoadedScenarioPack Load(string packName)
    {
        string root = FindRepositoryRoot();
        return new ScenarioPackLoader(
            Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
            Path.Combine(root, "config", "scenario-pack-trust-roots.json"),
            "v0.4.0-beta",
            "v1.5.0").Load(Path.Combine(root, "packs", "scenario-packs", packName, "1.0.0"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "baselines.lock.json")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
