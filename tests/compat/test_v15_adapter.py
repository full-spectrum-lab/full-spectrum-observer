"""T03 — Engine v1.5.0 adapter fidelity pass-through and reference-only passing.

Verifies the v1.5 Envelope is projected faithfully (UNKNOWN / digest / source
version / external_effect preserved), references are extracted (not merged into
Observation ownership), the hard gate is not downgraded, and cross-platform
serialization is stable (XPLAT-W/L).
"""

from __future__ import annotations

import json
import os

from compat.adapter_interface import AdaptationContext
from compat.canonical import sha256_hex
from compat.engine_v15_adapter import EngineV15Adapter
from compat.adapter_result import UNKNOWN

_GOLDEN = os.path.join(
    os.path.dirname(__file__), "fixtures", "v1.5_case005", "projected_v1.5.golden.json"
)


def _adapt(raw):
    ctx = AdaptationContext(observation_id=raw.get("observation_id", ""), source_version="1.5.0")
    return EngineV15Adapter().adapt(raw, ctx)


def test_v15_fidelity_pass_through(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    proj = result.projected_envelope

    assert proj.source_version == "1.5.0"
    # Faithful copy (not recomputed) of every supported section.
    assert isinstance(proj.subject_declaration, dict)
    assert proj.subject_declaration["local_subject_id"] == "subj-ec-cs-005"
    assert isinstance(proj.evaluation_events, list) and len(proj.evaluation_events) == 2
    assert isinstance(proj.replay_bundle, dict)
    assert proj.replay_bundle["bundle_id"] == "rb-005"
    assert isinstance(proj.rbac, dict)
    assert isinstance(proj.desensitization, dict)
    assert isinstance(proj.review, dict)
    assert isinstance(proj.resilience, dict)
    assert isinstance(proj.connector, dict)
    # No unsupported sections -> no UNKNOWN markers.
    assert proj.unknowns == []
    for section in (
        "subject_declaration",
        "evaluation_events",
        "replay_bundle",
        "rbac",
        "desensitization",
        "review",
        "resilience",
        "connector",
    ):
        assert getattr(proj, section) != UNKNOWN


def test_v15_external_effect_false(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    assert result.external_effect is False
    assert result.projected_envelope.external_effect is False


def test_v15_connector_write_off(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    assert result.projected_envelope.connector["write_enabled"] is False


def test_v15_hard_gate_not_downgraded(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    # Hard gate is preserved verbatim; never overridden by a composite score.
    assert result.projected_envelope.hard_gate["result"] == "PASS"
    assert result.projected_envelope.hard_gate["score"] == 0.81


def test_v15_references_extracted_not_owned(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    # References are extracted as handles (ownership stays with the Engine).
    assert len(result.event_refs) == 2
    assert len(result.replay_refs) == 1
    assert len(result.review_refs) == 1
    assert result.event_refs[0].event_id == "evt-005-a"
    assert result.replay_refs[0].bundle_id == "rb-005"
    assert result.review_refs[0].original_event_ref == "evt-005-a"
    # References resolve to real Engine entities.
    assert result.verify_refs_resolvable() is True


def test_v15_golden_byte_level_regression(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _adapt(raw)
    with open(_GOLDEN, "r", encoding="utf-8") as fh:
        golden = json.load(fh)
    assert result.projected_envelope.to_dict() == golden
    assert golden["canonical_digest"] == result.projected_envelope.canonical_digest


def test_v15_deterministic_rerun(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    first = _adapt(raw).projected_envelope.canonical_digest
    second = _adapt(raw).projected_envelope.canonical_digest
    assert first == second


def test_cross_platform_digest_stable(load_json):
    """XPLAT-W/L: canonical digest is identical regardless of host OS."""
    xplat = load_json("cross_platform/xplat_sample.json")
    # The digest is computed over the fixture minus the embedded expectation.
    digest = sha256_hex({k: v for k, v in xplat.items() if k != "expected_digest"})
    assert digest == xplat["expected_digest"]
    # Re-running yields the same digest (deterministic canonicalization).
    assert sha256_hex({k: v for k, v in xplat.items() if k != "expected_digest"}) == digest
