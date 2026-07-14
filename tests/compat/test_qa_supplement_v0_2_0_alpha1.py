"""QA supplement — blind-spot coverage for the v0.2.0-alpha.1 defect fixes.

These tests close gaps in ``test_defect_fixes_v0_2_0_alpha1.py``:

* D3 — positive path: the *real* trusted Engine anchors (v1.0.0 /
  v1.5.0) MUST resolve successfully (the fix must not over-reject valid
  baselines). Also documents the report's realistic forgery intent (valid
  declared version + forged tag -> ENGINE_PIN_MISMATCH).
* D5 — positive path: well-formed Engine/Observer envelopes MUST pass
  validation (the strict schema must not over-reject valid input).
* D6 — array / integer non-snapshot and non-dict inputs to ``resolve``
  and ``execute`` MUST return a structured ``UnsupportedVersionError``
  (the engineer's tests only covered ``None`` and ``str``).

These are additive; the original 44 + 20 suite continues to pass.
"""

from __future__ import annotations

import pytest

from compat.adapter_interface import AdaptationContext
from compat.adapter_result import EngineEnvelope, ObserverEnvelope
from compat.engine_facade import EngineFacade
from compat.engine_v15_adapter import EngineV15Adapter
from compat.runtime_snapshot import RuntimeConfigurationSnapshot
from compat.schema_validator import SchemaValidator
from compat.version_resolver import (
    REASON_ADAPTER_VERSION_BINDING,
    REASON_INPUT_TYPE_INVALID,
    REASON_SNAPSHOT_PIN_MISMATCH,
    EngineVersionResolver,
    UnsupportedVersionError,
)


# --------------------------------------------------------------------------
# D3 — positive path: real trusted anchors MUST resolve.
# --------------------------------------------------------------------------
def test_resolve_real_v1_0_0_anchor_passes():
    resolved = EngineVersionResolver.default().resolve(
        RuntimeConfigurationSnapshot.frozen_v1_0_0()
    )
    assert resolved.version == "1.0.0"


def test_resolve_real_v1_5_0_anchor_passes():
    resolved = EngineVersionResolver.default().resolve(
        RuntimeConfigurationSnapshot.frozen_v1_5_0()
    )
    assert resolved.version == "1.5.0"


def test_resolve_realistic_forgery_yields_pin_mismatch():
    # Report intent: declare a VALID supported version (1.5.0) but forge the
    # tag/commit/digest -> must be ENGINE_PIN_MISMATCH (fail-closed).
    forged = RuntimeConfigurationSnapshot(
        engine_version_declared="1.5.0",
        engine_tag="v9.9.9",
        engine_commit="garbage",
        engine_digest="not-a-sha256",
    )
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(forged)
    assert exc.value.reason_code == REASON_SNAPSHOT_PIN_MISMATCH


# --------------------------------------------------------------------------
# D5 — positive path: valid envelopes MUST pass validation.
# --------------------------------------------------------------------------
def test_validate_engine_accepts_valid_1_2_envelope():
    env = EngineEnvelope.build(
        envelope_version="1.2",
        payload={
            "envelope_version": "1.2",
            "source_version": "1.5.0",
            "profile_scenario": {"scenario_ref": "s1"},
            "evaluation_events": [],
            "unknowns": [],
        },
    )
    chk = SchemaValidator().validate_engine(env)
    assert chk.ok, chk.field_errors


def test_validate_observer_accepts_valid_envelope():
    obs = ObserverEnvelope(
        source_version="1.5.0",
        evaluation_events=["e1"],
        unknowns=["subject_declaration"],
        external_effect=False,
    ).finalize()
    chk = SchemaValidator().validate_observer(obs)
    assert chk.ok, chk.field_errors


# --------------------------------------------------------------------------
# D6 — array / integer inputs MUST return structured errors.
# --------------------------------------------------------------------------
def test_resolve_rejects_array():
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve([1, 2, 3])
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_resolve_rejects_integer():
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(123)
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_execute_rejects_array_raw(snapshot_v1_5):
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(snapshot_v1_5, [1, 2, 3])
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID


def test_execute_rejects_integer_raw(snapshot_v1_5):
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(snapshot_v1_5, 123)
    assert exc.value.reason_code == REASON_INPUT_TYPE_INVALID
