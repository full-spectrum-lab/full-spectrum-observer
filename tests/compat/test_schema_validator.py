"""T04 — Dual Schema validation (engine + observer), with UNKNOWN separation."""

from __future__ import annotations

from compat.adapter_interface import AdaptationContext
from compat.adapter_result import EngineEnvelope, ObserverEnvelope, UNKNOWN
from compat.engine_v1_adapter import EngineV1Adapter
from compat.engine_v15_adapter import EngineV15Adapter
from compat.schema_validator import SchemaValidator

UNSUPPORTED = (
    "subject_declaration",
    "evaluation_events",
    "replay_bundle",
    "rbac",
    "desensitization",
    "review",
    "resilience",
    "connector",
    "hard_gate",
)


def _v1_result(raw):
    ctx = AdaptationContext(observation_id="", source_version="1.0.0")
    return EngineV1Adapter().adapt(raw, ctx)


def _v15_result(raw):
    ctx = AdaptationContext(observation_id="", source_version="1.5.0")
    return EngineV15Adapter().adapt(raw, ctx)


def test_validate_engine_v1_ok(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _v1_result(raw)
    check = SchemaValidator().validate_engine(result.raw_envelope)
    assert check.ok is True
    assert check.field_errors == []


def test_validate_engine_v15_ok(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _v15_result(raw)
    check = SchemaValidator().validate_engine(result.raw_envelope)
    assert check.ok is True


def test_validate_observer_v1_unknowns_not_errors(load_json):
    raw = load_json("v1.0_golden/raw_v1.0.json")
    result = _v1_result(raw)
    check = SchemaValidator().validate_observer(result.projected_envelope)
    # Format is valid; UNKNOWN sections are governance semantics, not errors.
    assert check.ok is True
    assert check.field_errors == []
    for section in UNSUPPORTED:
        assert section in check.unknowns


def test_validate_observer_v15_ok(load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _v15_result(raw)
    check = SchemaValidator().validate_observer(result.projected_envelope)
    assert check.ok is True
    assert check.unknowns == []


def test_validate_observer_rejects_external_effect_true():
    bad = ObserverEnvelope(
        source_version="1.5.0",
        external_effect=True,  # redline R15
    ).finalize()
    check = SchemaValidator().validate_observer(bad)
    assert check.ok is False
    assert "OBS_EXTERNAL_EFFECT_MUST_BE_FALSE" in check.field_errors


def test_validate_observer_rejects_connector_write_on():
    bad = ObserverEnvelope(
        source_version="1.5.0",
        connector={"write_enabled": True},  # redline R07
    ).finalize()
    check = SchemaValidator().validate_observer(bad)
    assert check.ok is False
    assert "OBS_CONNECTOR_WRITE_ENABLED_MUST_BE_FALSE" in check.field_errors


def test_triple_references_retained(load_json):
    """raw / canonical / output triple is preserved for dual attestation."""
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = _v15_result(raw)
    # raw
    assert isinstance(result.raw_envelope, EngineEnvelope)
    assert isinstance(result.raw_envelope.payload, dict)
    # canonical
    assert result.raw_envelope.canonical_digest
    assert result.projected_envelope.canonical_digest
    # output
    assert isinstance(result.projected_envelope, ObserverEnvelope)
    # Schema validation produces structured results for both sides.
    ev = SchemaValidator().validate_engine(result.raw_envelope)
    ov = SchemaValidator().validate_observer(result.projected_envelope)
    assert ev.ok and ov.ok
