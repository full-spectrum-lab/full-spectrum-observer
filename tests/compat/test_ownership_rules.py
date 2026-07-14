"""T06 — EvaluationEvent / ReplayBundle / Review ownership rules.

The compat layer passes Engine-sourced events by **reference only**; it never
merges their ownership into the Observation. ``original_event_ref`` must resolve
to a real event — a fabricated reference is rejected as evidence forgery.
The ObservedSubject is never treated as an authentication principal.
"""

from __future__ import annotations

import pytest

from compat.adapter_interface import AdaptationContext
from compat.adapter_result import UNKNOWN
from compat.engine_v1_adapter import EngineV1Adapter
from compat.engine_v15_adapter import EngineV15Adapter
from compat.engine_facade import EngineFacade
from compat.version_resolver import REASON_REFERENCE_UNRESOLVABLE

# Auth-principal markers that must NEVER appear on an ObservedSubject declaration.
_AUTH_MARKERS = ("authenticated", "is_login", "is_principal", "login")


def _v1(raw):
    ctx = AdaptationContext(observation_id="", source_version="1.0.0")
    return EngineV1Adapter().adapt(raw, ctx)


def _v15(raw):
    ctx = AdaptationContext(observation_id="", source_version="1.5.0")
    return EngineV15Adapter().adapt(raw, ctx)


def test_v15_references_present_but_not_owned(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _v15(raw)
    # References are exposed as handles.
    assert result.event_refs and result.replay_refs and result.review_refs
    # Resolvable (ownership stays with the Engine).
    assert result.verify_refs_resolvable() is True
    # The projected envelope carries the same event content (audit transparency)
    # but the adapter must NOT have mutated it.
    assert result.projected_envelope.evaluation_events == raw["evaluation_events"]


def test_v1_has_no_refs_to_own(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _v1(raw)
    assert result.event_refs == []
    assert result.replay_refs == []
    assert result.review_refs == []
    # Nothing to own -> trivially resolvable.
    assert result.verify_refs_resolvable() is True


def test_fabricated_reference_is_rejected(load_json):
    """original_event_ref pointing at a non-existent event fails closed."""
    raw = load_json("neg/dependency_not_replayable.json")
    result = _v15(raw)
    assert result.verify_refs_resolvable() is False


def test_facade_rejects_fabricated_reference(
    v15_facade: EngineFacade, snapshot_v1_5, load_json
):
    raw = load_json("neg/dependency_not_replayable.json")
    with pytest.raises(Exception) as exc:
        v15_facade.execute(snapshot_v1_5, raw)
    # Structured fail-closed error, not a silent pass.
    assert exc.value.reason_code == REASON_REFERENCE_UNRESOLVABLE


def test_observed_subject_is_not_auth_principal(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _v15(raw)
    subject = result.projected_envelope.subject_declaration
    assert isinstance(subject, dict)
    for marker in _AUTH_MARKERS:
        assert marker not in subject


def test_external_effect_false_both_baselines(load_json):
    v1 = _v1(load_json("v1.0_golden/raw_v1.0.json"))
    v15 = _v15(load_json("v1.5_case005/envelope_v1.5.json"))
    assert v1.external_effect is False
    assert v15.external_effect is False
