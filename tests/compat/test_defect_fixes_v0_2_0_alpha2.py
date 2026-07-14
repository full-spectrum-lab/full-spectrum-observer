"""Targeted regression tests for the v0.2.0-alpha.2 release-blocking defects.

Closes the two boundary defects the third-party independent re-verification
found were still open (they were test-boundary omissions, not false-green):

* P1 (F-04) — Snapshot / Raw Envelope / Adapter three-way version binding.
  A raw Envelope that self-declares a conflicting ``source_version`` MUST be
  rejected with ``ENGINE_VERSION_MISMATCH`` (``REASON_VERSION_UNSUPPORTED``)
  before adaptation (fail-closed). The projected envelope and the result MUST
  both carry the trusted resolved version, and so must the dual attestation.
* P2 (F-08) — EvaluationEvent / Review reference integrity. An empty
  ``event_digest`` (``EvaluationEventRef``) or empty ``review_id``
  (``ReviewRef``) MUST be rejected with ``REASON_REFERENCE_UNRESOLVABLE``.

These are additive to the existing suite; together they lift the count to
>= 76 passed (original 73 must remain green).
"""

from __future__ import annotations

import pytest

from compat.adapter_interface import AdaptationContext
from compat.engine_facade import EngineFacade
from compat.engine_v15_adapter import EngineV15Adapter
from compat.runtime_snapshot import RuntimeConfigurationSnapshot
from compat.version_resolver import (
    REASON_REFERENCE_UNRESOLVABLE,
    REASON_VERSION_UNSUPPORTED,
    UnsupportedVersionError,
)


def _v15_facade() -> EngineFacade:
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    return facade


def _snapshot_v1_5() -> RuntimeConfigurationSnapshot:
    return RuntimeConfigurationSnapshot.frozen_v1_5_0()


def _base_raw(source_version: str | None = "1.5.0") -> dict:
    """Minimal faithful v1.5 raw output with resolvable references.

    ``source_version`` is omitted when ``None`` so the P1 Gate treats the raw
    as declaration-less and trusts the resolved contract (no false rejection).
    """
    raw: dict = {
        "envelope_version": "1.5",
        "observation_id": "obs-a2-001",
        "profile_scenario": {"scenario_ref": "s-a2"},
        "evaluation_events": [
            {
                "event_id": "evt-005-a",
                "event_digest": "sha256:evt-005-a",
                "capability": "v1.4:evaluation_event",
                "outcome": "logged",
            }
        ],
        "replay_bundle": {
            "bundle_id": "rb-005",
            "capability_level": "L3",
            "missing_deps": [],
        },
        "review": {
            "review_id": "rev-005",
            "original_event_ref": "evt-005-a",
        },
        "external_effect": False,
        "unknowns": [],
    }
    if source_version is not None:
        raw["source_version"] = source_version
    return raw


# --------------------------------------------------------------------------
# P1 (F-04) — three-way version binding: raw must not override resolved.
# --------------------------------------------------------------------------
def test_p1_raw_source_version_conflict_rejected():
    """Third-party repro: Snapshot=1.5.0 / Adapter=1.5.0 / raw=9.9.9 -> reject."""
    facade = _v15_facade()
    raw = _base_raw(source_version="9.9.9")
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(_snapshot_v1_5(), raw)
    assert exc.value.reason_code == REASON_VERSION_UNSUPPORTED
    # The structured message must surface both the conflicting raw and the
    # trusted resolved version so the rejection is auditable.
    assert "9.9.9" in exc.value.message
    assert "1.5.0" in exc.value.message


def test_p1_missing_raw_source_version_trusted():
    """Declaration-less raw passes and projects the resolved version."""
    facade = _v15_facade()
    result = facade.execute(_snapshot_v1_5(), _base_raw(source_version=None))
    assert result.source_version == "1.5.0"
    assert result.projected_envelope.source_version == "1.5.0"
    # The facade attestation also uses the trusted resolved version.
    assert facade.attestations[0].source_version == "1.5.0"


def test_p1_matching_raw_source_version_ok():
    """A raw whose self-declared version matches the resolved one passes."""
    facade = _v15_facade()
    result = facade.execute(_snapshot_v1_5(), _base_raw(source_version="1.5.0"))
    assert result.source_version == "1.5.0"
    assert result.projected_envelope.source_version == "1.5.0"


def test_p1_adapter_projects_trusted_version():
    """P1 layer-2 defence at the adapter unit level (independent of the facade
    Gate).

    Directly drives ``EngineV15Adapter.adapt`` with a raw that self-declares a
    *conflicting* ``source_version="9.9.9"`` while the trusted
    ``AdaptationContext.source_version`` is ``"1.5.0"``. The projection MUST
    use the trusted version, never the raw's conflicting value — proving the
    adapter fix is real even when the facade Gate pre-empts the call (QA
    mutation m2: the Gate hides this layer from facade-level tests, so the
    adapter unit layer is the durable backstop).
    """
    raw = {
        "source_version": "9.9.9",  # conflicting raw self-declaration
        "observation_id": "obs-unit-p1-001",
        "evaluation_events": [
            {"event_id": "evt-1", "event_digest": "sha256:evt-1"}
        ],
    }
    ctx = AdaptationContext(
        observation_id="obs-unit-p1-001",
        source_version="1.5.0",  # trusted resolved version
    )
    result = EngineV15Adapter().adapt(raw, ctx)
    # The projected envelope and the result carry the trusted version.
    assert result.source_version == "1.5.0"
    assert result.projected_envelope.source_version == "1.5.0"
    # Explicitly prove the conflicting raw value did NOT leak through.
    assert result.source_version != raw["source_version"]
    assert result.projected_envelope.source_version != raw["source_version"]


# --------------------------------------------------------------------------
# P2 (F-08) — reference integrity: mandatory non-empty reference fields.
# --------------------------------------------------------------------------
def test_p2_empty_event_digest_rejected():
    """Case A: evaluation_events entry with event_digest='' is rejected."""
    facade = _v15_facade()
    raw = _base_raw()
    raw["evaluation_events"][0]["event_digest"] = ""
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(_snapshot_v1_5(), raw)
    assert exc.value.reason_code == REASON_REFERENCE_UNRESOLVABLE


def test_p2_empty_review_id_rejected():
    """Case B: review with review_id='' is rejected."""
    facade = _v15_facade()
    raw = _base_raw()
    raw["review"]["review_id"] = ""
    with pytest.raises(UnsupportedVersionError) as exc:
        facade.execute(_snapshot_v1_5(), raw)
    assert exc.value.reason_code == REASON_REFERENCE_UNRESOLVABLE
