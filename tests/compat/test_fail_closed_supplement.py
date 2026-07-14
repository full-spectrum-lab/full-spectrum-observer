"""Supplemental coverage (QA Observer v0.2 — independent verification).

Closes the gap between the 7 defined ``UnsupportedVersionError`` reason codes
and the 4 exercised by the engineer's suite. These tests exercise real facade
code paths (dual Schema validation, dual attestation, UNKNOWN fidelity) so that
every reachable fail-closed branch is asserted at least once.

Covered here:
  * ``REASON_SCHEMA_ENGINE_INVALID`` — malformed v1.5 Envelope through facade.
  * ``REASON_SCHEMA_OBSERVER_INVALID`` — ``external_effect=True`` / connector
    ``write_enabled=True`` propagated through the facade.
  * Dual attestation retains both raw and projected digests (raw+projected 双存证).
  * v1.5 explicit UNKNOWN is preserved verbatim (never downgraded).
"""

from __future__ import annotations

import pytest

from compat.adapter_interface import AdaptationContext
from compat.adapter_result import UNKNOWN
from compat.engine_facade import EngineFacade
from compat.engine_v15_adapter import EngineV15Adapter
from compat.version_resolver import (
    REASON_SCHEMA_ENGINE_INVALID,
    REASON_SCHEMA_OBSERVER_INVALID,
)


@pytest.fixture
def v15_facade() -> EngineFacade:
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    return facade


def test_facade_schema_engine_invalid_raises(v15_facade, snapshot_v1_5):
    # Envelope declares v1.2 but omits the required ``source_version`` key,
    # so SchemaValidator.validate_engine must fail closed.
    raw = {
        "envelope_version": "1.2",
        "observation_id": "obs-bad-engine-001",
        "profile_scenario": {"scenario_ref": "x"},
    }
    with pytest.raises(Exception) as exc:
        v15_facade.execute(snapshot_v1_5, raw)
    assert exc.value.reason_code == REASON_SCHEMA_ENGINE_INVALID


def test_facade_schema_observer_external_effect_true_raises(v15_facade, snapshot_v1_5):
    raw = {
        "envelope_version": "1.5",
        "source_version": "1.5.0",
        "observation_id": "obs-bad-ext-001",
        "external_effect": True,  # redline R15 — must never be True
    }
    with pytest.raises(Exception) as exc:
        v15_facade.execute(snapshot_v1_5, raw)
    assert exc.value.reason_code == REASON_SCHEMA_OBSERVER_INVALID


def test_facade_schema_observer_connector_write_on_raises(v15_facade, snapshot_v1_5):
    raw = {
        "envelope_version": "1.5",
        "source_version": "1.5.0",
        "observation_id": "obs-bad-conn-001",
        "connector": {"write_enabled": True},  # redline R07 — must stay OFF
    }
    with pytest.raises(Exception) as exc:
        v15_facade.execute(snapshot_v1_5, raw)
    assert exc.value.reason_code == REASON_SCHEMA_OBSERVER_INVALID


def test_facade_dual_attestation_retains_both_digests(v15_facade, snapshot_v1_5, load_json):
    raw = load_json("v1.5_case005/envelope_v1.5.json")
    result = v15_facade.execute(snapshot_v1_5, raw)
    atts = v15_facade.attestations
    assert len(atts) == 1
    att = atts[0]
    # Both the raw (Engine output) and projected (Observer) digests are retained.
    assert att.raw_digest == result.raw_envelope.canonical_digest
    assert att.projected_digest == result.projected_envelope.canonical_digest
    # They are different envelope schemas, so the digests must differ.
    assert att.raw_digest != att.projected_digest
    assert att.external_effect is False


def test_v15_preserves_explicit_unknown_not_downgraded(load_json):
    raw = dict(load_json("v1.5_case005/envelope_v1.5.json"))
    # Engine itself declares this section as UNKNOWN.
    raw["resilience"] = UNKNOWN
    ctx = AdaptationContext(observation_id="obs-unk-001", source_version="1.5.0")
    result = EngineV15Adapter().adapt(raw, ctx)
    # The UNKNOWN marker is faithfully preserved — never downgraded/zero-filled.
    assert result.projected_envelope.resilience == UNKNOWN
    assert "resilience" in result.projected_envelope.unknowns
    assert result.verify_refs_resolvable() is True
