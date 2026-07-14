"""T01 — Version negotiation and fail-closed error contract.

Covers positive resolution for both baselines and the three mandated fail-closed
paths: unsupported version, missing adapter, and unresolvable/replayable
dependency (fabricated reference). All failures surface as a structured
:class:`UnsupportedVersionError` — never a silent fallback.
"""

from __future__ import annotations

import pytest

from compat.engine_facade import EngineFacade
from compat.runtime_snapshot import RuntimeConfigurationSnapshot
from compat.version_resolver import (
    REASON_ADAPTER_MISSING,
    REASON_REFERENCE_UNRESOLVABLE,
    REASON_SNAPSHOT_FLOATING,
    REASON_VERSION_UNSUPPORTED,
    EngineVersionResolver,
    UnsupportedVersionError,
)

# Capability constants used only for readability in assertions.
from compat.engine_version import CAP_CONNECTOR, CAP_SUBJECT_DECLARATION


def test_is_supported_matrix():
    resolver = EngineVersionResolver.default()
    assert resolver.is_supported("1.0.0") is True
    assert resolver.is_supported("1.5.0") is True
    assert resolver.is_supported("9.9.9") is False


def test_resolve_v1_0_capabilities():
    snap = RuntimeConfigurationSnapshot.frozen_v1_0_0()
    version = EngineVersionResolver.default().resolve(snap)
    assert version.version == "1.0.0"
    assert version.contract_baseline == "v1.0"
    assert version.supports_capability(CAP_SUBJECT_DECLARATION) is False
    assert version.supports_capability(CAP_CONNECTOR) is False


def test_resolve_v1_5_capabilities():
    snap = RuntimeConfigurationSnapshot.frozen_v1_5_0()
    version = EngineVersionResolver.default().resolve(snap)
    assert version.version == "1.5.0"
    assert version.contract_baseline == "v1.5"
    assert version.supports_capability(CAP_SUBJECT_DECLARATION) is True
    assert version.supports_capability(CAP_CONNECTOR) is True


def test_unsupported_version_raises_fail_closed():
    snap = RuntimeConfigurationSnapshot(
        engine_version_declared="9.9.9",
        engine_tag="v9.9.9",
        engine_commit="deadbeef",
        engine_digest="sha256:deadbeef",
    )
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(snap)
    err = exc.value
    assert err.requested_version == "9.9.9"
    assert err.reason_code == REASON_VERSION_UNSUPPORTED
    env = err.to_envelope()
    assert env["error"] is True
    assert env["kind"] == "UnsupportedVersionError"
    assert env["external_effect"] is False


def test_floating_snapshot_rejected():
    snap = RuntimeConfigurationSnapshot(
        engine_version_declared="1.5.0",
        engine_tag="v1.5.0",
        engine_commit="latest",  # floating -> forbidden
        engine_digest="sha256:abc",
    )
    assert snap.validate_self() is False
    with pytest.raises(UnsupportedVersionError) as exc:
        EngineVersionResolver.default().resolve(snap)
    assert exc.value.reason_code == REASON_SNAPSHOT_FLOATING


def test_adapter_missing_fail_closed(v15_facade: EngineFacade, snapshot_v1_0):
    # v15_facade only registers the 1.5.0 adapter; requesting 1.0.0 must fail.
    raw = {"observation_id": "obs-x", "scenario": "s"}
    with pytest.raises(UnsupportedVersionError) as exc:
        v15_facade.execute(snapshot_v1_0, raw)
    assert exc.value.reason_code == REASON_ADAPTER_MISSING


def test_dependency_not_replayable_fail_closed(
    v15_facade: EngineFacade, snapshot_v1_5, load_json
):
    # Neg fixture carries a fabricated original_event_ref -> unresolvable.
    raw = load_json("neg/dependency_not_replayable.json")
    with pytest.raises(UnsupportedVersionError) as exc:
        v15_facade.execute(snapshot_v1_5, raw)
    assert exc.value.reason_code == REASON_REFERENCE_UNRESOLVABLE
