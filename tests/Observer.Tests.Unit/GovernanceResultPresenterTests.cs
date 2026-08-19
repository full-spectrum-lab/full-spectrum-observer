using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Host.Web.Services;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="GovernanceResultPresenter"/> — the pure, Razor-independent function
/// shared by the NewAnalysis and AnalysisRecords result views. Covers F2 (execution/governance
/// separation), F3 (pure-UNKNOWN fixed fallback), and F4 (conclusion pass-through).
/// </summary>
public sealed class GovernanceResultPresenterTests
{
    private static AnalysisResult MakeResult(string unknownState, bool hardGate = false) => new()
    {
        ResultId = "RES-1",
        TaskId = "T-1",
        ConclusionPayload = "{}",
        UnknownState = unknownState,
        HardGate = hardGate,
        CreatedAt = "2026-01-01T00:00:00Z",
    };

    /// <summary>
    /// The three fixed lines for a pure UNKNOWN/PARTIAL state (no Engine conflict signals).
    /// Hoisted to a static readonly field to satisfy CA1861 (avoid repeated inline array allocations).
    /// </summary>
    private static readonly string[] PureUnknownFixedLines =
    {
        "Engine 未提供原因码。",
        "Engine 未提供待补上下文。",
        "本次输出未指定复核责任人。",
    };

    // F2-1: Present("Completed") execution view model does NOT carry the governance `known` green class.
    [Fact]
    public void Present_Completed_execution_state_uses_neutral_class_not_known_green()
    {
        var exec = GovernanceResultPresenter.Present("Completed");
        exec.CssClass.Should().Be("exec-completed");
        exec.CssClass.Should().NotBe("known");
        exec.CssClass.Should().NotContain("gov-known");
    }

    // F2-2: UnknownState == UNKNOWN => governance CSS class differs from execution CSS class.
    [Fact]
    public void Present_UNKNOWN_governance_class_differs_from_execution_class()
    {
        var result = MakeResult("UNKNOWN");
        var exec = GovernanceResultPresenter.Present("Completed");
        var gov = GovernanceResultPresenter.Present(result);
        gov.CssClass.Should().Be("gov-unknown");
        gov.CssClass.Should().NotBe(exec.CssClass);
    }

    // F2-3: execution Completed must never render with governance `known` green semantics.
    [Fact]
    public void Execution_Completed_and_governance_KNOWN_use_different_namespaces()
    {
        var exec = GovernanceResultPresenter.PresentExecutionState("Completed");
        var gov = GovernanceResultPresenter.PresentGovernanceState("KNOWN", false);
        exec.CssClass.Should().StartWith("exec-");
        gov.CssClass.Should().StartWith("gov-");
        exec.CssClass.Should().NotBe(gov.CssClass);
    }

    // F3-1: pure UNKNOWN (no ReasonCode/MissingContext/HumanReviewRequired) => exactly the three
    // fixed lines, with no Observer-generated reason or suggested action.
    [Fact]
    public void Pure_UNKNOWN_explanation_is_three_fixed_lines_only()
    {
        var result = MakeResult("UNKNOWN");
        var lines = GovernanceResultPresenter.BuildUnknownStateExplanation(
            result.UnknownState, new List<ConflictObservation>());

        lines.Should().HaveCount(3);
        lines[0].Should().Be("Engine 未提供原因码。");
        lines[1].Should().Be("Engine 未提供待补上下文。");
        lines[2].Should().Be("本次输出未指定复核责任人。");

        string joined = string.Join("\n", lines);
        joined.Should().NotContain("建议");   // no Observer-generated suggested action
        joined.Should().NotContain("原因可能是"); // no inferred reason
        joined.Should().NotContain("下一步请");   // no generated next-step directive
    }

    // F3-2: PARTIAL without any conflict signals also falls back to the three fixed lines.
    [Fact]
    public void Pure_PARTIAL_without_conflicts_is_three_fixed_lines()
    {
        var lines = GovernanceResultPresenter.BuildUnknownStateExplanation(
            "PARTIAL", new List<ConflictObservation>());
        lines.Should().BeEquivalentTo(PureUnknownFixedLines);
    }

    // F3-3: non-UNKNOWN/PARTIAL states produce no explanation block.
    [Fact]
    public void KNOWN_state_produces_no_explanation()
    {
        var lines = GovernanceResultPresenter.BuildUnknownStateExplanation(
            "KNOWN", new List<ConflictObservation>());
        lines.Should().BeEmpty();
    }

    // F3-4: when conflicts carry Engine fields, those are surfaced verbatim (pass-through).
    [Fact]
    public void Conflicts_with_engine_fields_surfaced_verbatim_not_invented()
    {
        var conflicts = new List<ConflictObservation>
        {
            new()
            {
                ObservationId = "OBS-1",
                ResultId = "RES-1",
                ConflictType = "X",
                InvolvedSubjects = System.Collections.Immutable.ImmutableArray.Create("S1"),
                Severity = "LOW",
                HumanReviewRequired = true,
                ReasonCode = "RC-42",
                MissingContext = System.Collections.Immutable.ImmutableArray.Create("ctx-a"),
            },
        };
        var lines = GovernanceResultPresenter.BuildUnknownStateExplanation("UNKNOWN", conflicts);
        lines.Should().Contain("Engine 原因码：RC-42（按原样呈现，不重算）。");
        lines.Should().Contain("Engine 待补上下文：ctx-a（按原样呈现，不重算）。");
        lines.Should().Contain("需要人工复核（Engine 标注；不指定具体责任人，不代表任何主体运营方被自动指派为复核人）。");
    }

    // F4-1: conclusion readable view decoded text == ConclusionPayload original (HTML-decode safe).
    [Fact]
    public void ConclusionReadableView_text_equals_original_payload()
    {
        const string payload = "{\"conclusion\":\"UNKNOWN <x> & \\\"q\\\" \\n tab\",\"score\":0.42}";
        var model = GovernanceResultPresenter.PresentConclusion(payload);
        model.VerbatimPayload.Should().Be(payload);
        model.ReadableText.Should().Be(payload); // pass-through; decoding yields the original
        WebUtility.HtmlDecode(model.ReadableText).Should().Be(payload);
        model.IsValidJson.Should().BeTrue();
        model.KeyValues.Should().NotBeNull();
        model.KeyValues!.Count.Should().Be(2);
    }

    // F4-2: non-JSON payload is preserved verbatim and not mis-parsed.
    [Fact]
    public void ConclusionReadableView_non_json_preserved_verbatim()
    {
        const string payload = "plain text conclusion, not json";
        var model = GovernanceResultPresenter.PresentConclusion(payload);
        model.VerbatimPayload.Should().Be(payload);
        model.ReadableText.Should().Be(payload);
        model.IsValidJson.Should().BeFalse();
        model.KeyValues.Should().BeNull();
    }
}
