"""Targeted regression tests for the v0.2.0-alpha.1 release-blocking defects.

These tests prove the seven defects (D1–D7) reported by the independent QA
verification are closed. They are additive — the original 44-test suite must
continue to pass; these guard the specific fail-closed behaviours.
"""

from __future__ import annotations

import dataclasses
import math

import pytest

from compat.adapter_result import EngineEnvelope, ObserverEnvelope, UNKNOWN
from compat.canonical import canonical_json
from compat.engine_facade import EngineFacade
from compat.engine_v1_adapter import EngineV1Adapter
from compat.engine_v15_adapter import EngineV15Adapter
from compat.runtime_snapshot import (
    TRUSTED_ENGINE_RELEASES,
    RuntimeConfigurationSnapshot,
)
from compat.schema_validator import SchemaValidator
from compat.version_resolver import (
    REASON_ADAPTER_VERSION_BINDING,
    REASON_INPUT_TYPE_INVALID,
    REASON_SNAPSHOT_PIN_MISMATCH,
    EngineVersionResolver,
    UnsupportedVersionError,
)


# --------------------------------------------------------------------------
# D3 — Snapshot must strictly pin the trusted Engine release (no forgery).
# --------------------------------------------------------------------------
def test_frozen_snapshots_use_real_trusted_digests():
    by_tag = {r.tag: r for r in TRUSTED_ENGINE_RELEASES}
    v1 = RuntimeConfigurationSnapshot.frozen_v1_0_0()
    v15 = RuntimeConfigurationSnapshot.frozen_v1_5_0()
    assert v1.engine_tag == "v1.0.0"
    assert v1.engine_commit == by_tag["v1.0.0"].commit
    assert v1.engine_digest == by_tag["v1.0.0"].digest
    assert v15.engine_tag == "v1.5.0"
    assert v15.engine_commit == by_tag["v1.5.0"].commit
    assert v15.engine_digest == by_tag["v1.5.0"].digest
    # No frozen / pending placeholders remain.
    assert "pending" not in v15.engine_digest and "frozen" not in v15.engine_digest


def test_resolver_rejects_forgotten_engine_commit(snapshot_v1_5):
    # Declared v1.5.0 but the commit is garbage -> pin mismatch (fail-closed).
    forged = dataclasses.replace(snapshot_v1_5, engine_commit="garbage")
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(forged)
    assert exc.value.reason_code == REASON_SNAPSHOT_PIN_MISMATCH


def test_resolver_rejects_forgotten_engine_digest(snapshot_v1_5):
    forged = dataclasses.replace(snapshot_v1_5, engine_digest="not-a-sha256")
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(forged)
    assert exc.value.reason_code == REASON_SNAPSHOT_PIN_MISMATCH


def test_resolver_rejects_untrusted_engine_tag(snapshot_v1_5):
    # tag v9.9.9 is not a published, trusted release.
    forged = dataclasses.replace(
        snapshot_v1_5,
        engine_tag="v9.9.9",
        engine_commit="garbage",
        engine_digest="not-a-sha256",
    )
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(forged)
    assert exc.value.reason_code == REASON_SNAPSHOT_PIN_MISMATCH


# --------------------------------------------------------------------------
# D4 — adapter version binding is strict (no V1 adapter under 1.5, etc.).
# --------------------------------------------------------------------------
def test_register_adapter_version_binding_rejected():
    facade = EngineFacade()
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.register_adapter("1.5.0", EngineV1Adapter())
    assert exc.value.reason_code == REASON_ADAPTER_VERSION_BINDING


def test_register_adapter_reverse_binding_rejected():
    facade = EngineFacade()
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.register_adapter("1.0.0", EngineV15Adapter())
    assert exc.value.reason_code == REASON_ADAPTER_VERSION_BINDING


def test_register_adapter_correct_binding_ok():
    facade = EngineFacade()
    # Must not raise: EngineV1Adapter bound to 1.0.0.
    facade.register_adapter("1.0.0", EngineV1Adapter())
    facade.register_adapter("1.5.0", EngineV15Adapter())


def test_register_adapter_rejects_non_adapter():
    facade = EngineFacade()
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.register_adapter("1.0.0", "not-an-adapter")
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


# --------------------------------------------------------------------------
# D5 — strict dual schema validation (no silent coercion).
# --------------------------------------------------------------------------
def test_validate_observer_rejects_unknowns_string():
    bad = ObserverEnvelope(source_version="1.5.0", unknowns="abc")  # type: ignore[arg-type]
    check = SchemaValidator().validate_observer(bad)
    assert check.ok is False
    assert "OBS_UNKNOWNS_TYPE_INVALID" in check.field_errors


def test_validate_observer_rejects_evaluation_events_string():
    bad = ObserverEnvelope(
        source_version="1.5.0", evaluation_events="not-a-list"  # type: ignore[arg-type]
    )
    check = SchemaValidator().validate_observer(bad)
    assert check.ok is False
    assert "OBS_EVALUATION_EVENTS_TYPE_INVALID" in check.field_errors


def test_validate_engine_rejects_evil_envelope_version():
    env = EngineEnvelope.build(envelope_version="evil-envelope", payload={})
    check = SchemaValidator().validate_engine(env)
    assert check.ok is False
    assert "ENGINE_ENVELOPE_VERSION_UNSUPPORTED" in check.field_errors


def test_adapter_does_not_split_unknowns_string():
    # The adapter must NOT silently turn unknowns="abc" into ["a","b","c"].
    result = EngineV15Adapter().adapt({"unknowns": "abc"}, _ctx("1.5.0"))
    assert result.projected_envelope.unknowns == "abc"
    assert result.projected_envelope.unknowns != ["a", "b", "c"]


def test_facade_rejects_unknowns_string(v15_facade, snapshot_v1_5):
    # End-to-end: a malformed unknowns must fail closed, not pass.
    with pytest.raises(UnsupportedVersionError):
        v15_facade.execute(snapshot_v1_5, {"unknowns": "abc"})


def _ctx(version: str):
    from compat.adapter_interface import AdaptationContext

    return AdaptationContext(observation_id="obs", source_version=version)


# --------------------------------------------------------------------------
# D6 — non-expected input types return structured errors, not raw exceptions.
# --------------------------------------------------------------------------
def test_resolver_rejects_non_snapshot_input():
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve("garbage")
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_resolver_rejects_none_snapshot():
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(None)
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_facade_execute_rejects_non_dict_raw(snapshot_v1_5):
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(snapshot_v1_5, "not-a-dict")
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_facade_execute_rejects_none_snapshot():
    facade = EngineFacade()
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(None, {})
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


# --------------------------------------------------------------------------
# D7 — canonical JSON rejects non-finite floats (NaN / Infinity).
# --------------------------------------------------------------------------
def test_canonical_json_rejects_nan():
    with pytest.raises(ValueError):
        canonical_json({"x": float("nan")})


def test_canonical_json_rejects_infinity():
    with pytest.raises(ValueError):
        canonical_json({"x": float("inf")})


def test_canonical_json_rejects_negative_infinity():
    with pytest.raises(ValueError):
        canonical_json({"x": float("-inf")})
