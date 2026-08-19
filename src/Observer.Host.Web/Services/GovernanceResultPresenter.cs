using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;

#nullable enable

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Pure, Razor-independent presenter that splits an analysis result into a SEPARATE
/// execution-state view model and governance-state view model, builds a pass-through
/// UNKNOWN/PARTIAL explanation block, and exposes a read-only (never recomputed) view of the
/// Engine conclusion.
///
/// Every method is a pure function over its inputs. It NEVER re-computes, downgrades, or rewrites
/// the Engine's conclusion (red lines #6 / #9), NEVER attributes a reviewer, and NEVER invents a
/// new governance conclusion. It is fully unit-testable without any Blazor dependency.
/// </summary>
public static class GovernanceResultPresenter
{
    // Fixed, verbatim fallback lines for a PURE UNKNOWN/PARTIAL state. Observer MUST NOT infer a
    // reason or generate a "next action" list beyond these three lines (F3 hard constraint).
    private static readonly IReadOnlyList<string> PureUnknownFallback = new[]
    {
        "Engine 未提供原因码。",
        "Engine 未提供待补上下文。",
        "本次输出未指定复核责任人。",
    };

    /// <summary>Execution-state view model: neutral styling, never reuses the governance `known` green.</summary>
    public sealed record ExecutionStateViewModel(string StatusText, string CssClass);

    /// <summary>Governance-state view model: a SEPARATE style namespace (`gov-*`) from execution.</summary>
    public sealed record GovernanceStateViewModel(
        string UnknownState, string CssClass, string HardGateText, string HardGateCssClass);

    /// <summary>Read-only (never rewritten) view of the Engine conclusion.</summary>
    /// <param name="VerbatimPayload">The Engine conclusion EXACTLY as stored — never mutated.</param>
    /// <param name="IsValidJson">True when the payload parsed as a JSON object (helper table only).</param>
    /// <param name="KeyValues">Top-level key/value pairs for a read-only helper view; null when not JSON.</param>
    /// <param name="ReadableText">Decoded readable text; ALWAYS equals <see cref="VerbatimPayload"/> (pass-through).</param>
    public sealed record ConclusionReadableModel(
        string VerbatimPayload, bool IsValidJson, IReadOnlyList<ConclusionKeyValue>? KeyValues, string ReadableText);

    /// <summary>A single top-level key/value of the Engine conclusion, surfaced verbatim.</summary>
    public sealed record ConclusionKeyValue(string Key, string Value);

    /// <summary>Convenience overload: present the EXECUTION state from a raw status string.</summary>
    public static ExecutionStateViewModel Present(string executionStatus) => PresentExecutionState(executionStatus);

    /// <summary>Convenience overload: present the GOVERNANCE state from an <see cref="AnalysisResult"/>.</summary>
    public static GovernanceStateViewModel Present(AnalysisResult result) =>
        PresentGovernanceState(result.UnknownState, result.HardGate);

    /// <summary>
    /// Maps a task EXECUTION status (Completed / Draft / RECOVERY_REQUIRED / ENGINE_FAILED / …) to a
    /// neutral view model. The CSS class is in the `exec-*` namespace and NEVER carries the
    /// governance `known` green semantics.
    /// </summary>
    public static ExecutionStateViewModel PresentExecutionState(string status)
    {
        string s = status ?? string.Empty;
        return s switch
        {
            "Completed" => new ExecutionStateViewModel("已完成（COMPLETED）", "exec-completed"),
            "Draft" => new ExecutionStateViewModel("草稿（DRAFT）", "exec-draft"),
            "RECOVERY_REQUIRED" or "ENGINE_FAILED" or "OUTPUT_VALIDATION_FAILED" or "PREFLIGHT_FAILED"
                => new ExecutionStateViewModel(s, "exec-failed"),
            _ => new ExecutionStateViewModel(string.IsNullOrWhiteSpace(s) ? "未知（UNKNOWN STATUS）" : s, "exec-draft"),
        };
    }

    /// <summary>
    /// Maps the GOVERNANCE conclusion (UnknownState / HardGate) to a SEPARATE view model. The CSS
    /// class is in the `gov-*` namespace; UNKNOWN/PARTIAL use neutral/warning color and NEVER reuse
    /// the `known` green. KNOWN may use green because it IS a favorable governance conclusion.
    /// </summary>
    public static GovernanceStateViewModel PresentGovernanceState(string unknownState, bool hardGate)
    {
        string state = unknownState ?? string.Empty;
        string css = state switch
        {
            "KNOWN" => "gov-known",
            "UNKNOWN" => "gov-unknown",
            "PARTIAL" => "gov-partial",
            _ => "gov-unknown",
        };
        string hardGateText = hardGate ? "硬门禁已触发（HARD_GATE）" : "未触发";
        string hardGateCss = hardGate ? "gov-hardgate" : "gov-soft";
        return new GovernanceStateViewModel(state, css, hardGateText, hardGateCss);
    }

    /// <summary>
    /// Builds the UNKNOWN/PARTIAL explanation block (pass-through only). Returns an EMPTY list when
    /// the governance state is not UNKNOWN/PARTIAL. For a PURE state (no Engine ReasonCode /
    /// MissingContext / HumanReviewRequired anywhere) it returns EXACTLY the three fixed fallback
    /// lines and invents nothing. When Engine provided those fields on a conflict observation, they
    /// are surfaced verbatim (never recomputed, never summarized into a new conclusion).
    /// </summary>
    public static IReadOnlyList<string> BuildUnknownStateExplanation(
        string unknownState, IReadOnlyList<ConflictObservation> conflicts)
    {
        string state = unknownState ?? string.Empty;
        if (state != "UNKNOWN" && state != "PARTIAL")
        {
            return new List<string>();
        }

        bool hasReasonCode = conflicts.Any(c => !string.IsNullOrWhiteSpace(c.ReasonCode));
        bool hasMissingContext = conflicts.Any(c => c.MissingContext is { } m && m.Length > 0);
        bool hasHumanReview = conflicts.Any(c => c.HumanReviewRequired);

        // Pure UNKNOWN/PARTIAL: Output ONLY the three fixed lines. Observer must not infer or
        // generate a reason / next-action list.
        if (!hasReasonCode && !hasMissingContext && !hasHumanReview)
        {
            return PureUnknownFallback.ToList();
        }

        // Engine provided at least one of the fields -> surface them verbatim (pass-through).
        var lines = new List<string>();
        foreach (var c in conflicts)
        {
            if (!string.IsNullOrWhiteSpace(c.ReasonCode))
            {
                lines.Add($"Engine 原因码：{c.ReasonCode}（按原样呈现，不重算）。");
            }
            if (c.MissingContext is { } m && m.Length > 0)
            {
                lines.Add("Engine 待补上下文：" + string.Join("；", m) + "（按原样呈现，不重算）。");
            }
            if (c.HumanReviewRequired)
            {
                lines.Add("需要人工复核（Engine 标注；不指定具体责任人，不代表任何主体运营方被自动指派为复核人）。");
            }
        }

        // Defensive: if the guard above let us through but no lines were produced, fall back to the
        // three fixed lines so we never emit an empty or invented block.
        return lines.Count > 0 ? lines : PureUnknownFallback.ToList();
    }

    /// <summary>
    /// Produces a read-only view of the Engine conclusion. The payload is NEVER rewritten or
    /// re-serialized; <see cref="ConclusionReadableModel.VerbatimPayload"/> and
    /// <see cref="ConclusionReadableModel.ReadableText"/> are always the original string. When the
    /// payload is valid JSON a helper key/value list is provided for display ONLY — it carries no
    /// new governance meaning (F4 red line: no recompute, no new conclusion field).
    /// </summary>
    public static ConclusionReadableModel PresentConclusion(string conclusionPayload)
    {
        string payload = conclusionPayload ?? string.Empty;
        IReadOnlyList<ConclusionKeyValue>? keyValues = null;
        bool isValidJson = false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                isValidJson = true;
                var pairs = new List<ConclusionKeyValue>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    // Value is rendered verbatim (GetRawText preserves the original JSON fragment).
                    pairs.Add(new ConclusionKeyValue(prop.Name, prop.Value.GetRawText()));
                }
                keyValues = pairs;
            }
        }
        catch (JsonException)
        {
            isValidJson = false;
        }

        return new ConclusionReadableModel(payload, isValidJson, keyValues, payload);
    }
}
