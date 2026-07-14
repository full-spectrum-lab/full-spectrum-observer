"""T02 — Engine v1.0.0 adapter projection and UNKNOWN fidelity."""

from __future__ import annotations

import json
import os

from compat.adapter_interface import AdaptationContext
from compat.canonical import sha256_hex
from compat.engine_v1_adapter import EngineV1Adapter
from compat.adapter_result import UNKNOWN

_FIXTURE = os.path.join(
    os.path.dirname(__file__), "fixtures", "v1.0_golden", "raw_v1.0.json"
)
_GOLDEN = os.path.join(
    os.path.dirname(__file__), "fixtures", "v1.0_golden", "projected_v1.0.golden.json"
)


def _adapt(raw):
    ctx = AdaptationContext(observation_id=raw.get("observation_id", ""), source_version="1.0.0")
    return EngineV1Adapter().adapt(raw, ctx)


def test_v1_projection_marks_unsupported_unknown(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _adapt(raw)
    proj = result.projected_envelope

    assert proj.source_version == "1.0.0"
    # v1.0 has no v1.2-v1.5 sections -> explicit UNKNOWN, never dropped.
    assert proj.subject_declaration == UNKNOWN
    assert proj.evaluation_events == UNKNOWN
    assert proj.replay_bundle == UNKNOWN
    assert proj.rbac == UNKNOWN
    assert proj.desensitization == UNKNOWN
    assert proj.review == UNKNOWN
    assert proj.resilience == UNKNOWN
    assert proj.connector == UNKNOWN
    assert proj.hard_gate == UNKNOWN
    # v1.0 narrow profile/gate is projected when present.
    assert isinstance(proj.profile_scenario, dict)
    assert proj.profile_scenario["scenario_ref"] == "local-priority-narrow"


def test_v1_external_effect_false(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _adapt(raw)
    assert result.external_effect is False
    assert result.projected_envelope.external_effect is False


def test_v1_unknowns_listed(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _adapt(raw)
    for section in (
        "subject_declaration",
        "evaluation_events",
        "replay_bundle",
        "rbac",
        "desensitization",
        "review",
        "resilience",
        "connector",
        "hard_gate",
    ):
        assert section in result.unknowns
    # No Engine events -> no references to own.
    assert result.event_refs == []
    assert result.replay_refs == []
    assert result.review_refs == []


def test_v1_raw_digest_deterministic(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _adapt(raw)
    assert result.raw_envelope.canonical_digest == sha256_hex(raw)


def test_v1_golden_byte_level_regression(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _adapt(raw)
    with open(_GOLDEN, "r", encoding="utf-8") as fh:
        golden = json.load(fh)
    # Full envelope equality (byte-level regression baseline).
    assert result.projected_envelope.to_dict() == golden
    # Stored canonical digest matches the re-computed digest (deterministic).
    assert golden["canonical_digest"] == result.projected_envelope.canonical_digest


def test_v1_deterministic_rerun(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    first = _adapt(raw).projected_envelope.canonical_digest
    second = _adapt(raw).projected_envelope.canonical_digest
    assert first == second
