from __future__ import annotations

import os

import pytest

from observer_engine2.adapter import Engine2ObserverAdapter, InMemoryProjectionAudit
from observer_engine2.messages import SnapshotBinding
from observer_engine2.runtime import PINNED_ENGINE_RUNTIME_COMMIT, PinnedEngineRuntimePort


ENGINE_CHECKOUT = os.environ.get("FSP_ENGINE2_PINNED_CHECKOUT")
pytestmark = pytest.mark.skipif(not ENGINE_CHECKOUT, reason="FSP_ENGINE2_PINNED_CHECKOUT is not configured")


def handler(request):
    descriptor = request.protocol_object["descriptor"]
    if descriptor == "unknown":
        return "UNKNOWN", "UNKNOWN", "UNKNOWN"
    if descriptor == "blocked":
        return "DENY", "PINNED_RUNTIME_TEST", "FAIL"
    return "ALLOW", "PINNED_RUNTIME_TEST", "PASS"


def build(adapter, request_id, key, descriptor):
    return adapter.build_request(
        request_id=request_id,
        idempotency_key=key,
        protocol_object={"descriptor": descriptor},
        snapshot_binding=SnapshotBinding(PINNED_ENGINE_RUNTIME_COMMIT, "5f5206d56d8ebf85720fcae031249b25b3ec2551", "869f61bd7bb970a2743058a52fe1e23570667028", "0.3.0-rc", "fixture-snapshot"),
    )


def test_exact_engine_runtime_allow_deny_unknown_idempotency_and_replay_failure():
    runtime = PinnedEngineRuntimePort(ENGINE_CHECKOUT, handler)
    adapter = Engine2ObserverAdapter(runtime, InMemoryProjectionAudit([]))
    allow = adapter.observe(build(adapter, "req-allow", "idem-allow", "allow"))
    deny = adapter.observe(build(adapter, "req-deny", "idem-deny", "blocked"))
    unknown = adapter.observe(build(adapter, "req-unknown", "idem-unknown", "unknown"))
    assert allow["engine_response"]["decision"] == "ALLOW"
    assert deny["engine_response"]["hard_gate"] == "FAIL"
    assert unknown["engine_response"]["decision"] == "UNKNOWN"
    repeated = adapter.observe(build(adapter, "req-retry", "idem-allow", "allow"))
    assert repeated["engine_response"] == allow["engine_response"]
    conflict = adapter.observe(build(adapter, "req-conflict", "idem-allow", "changed"))
    assert conflict["engine_response"]["error_code"] == "IDEMPOTENCY_CONFLICT"
    replay = runtime.replay(
        replay_id="replay-missing",
        original_event_ref="missing",
        evidence_manifest_sha256="BAD",
        expected_input_digest="BAD",
        expected_output_digest="BAD",
    )
    assert replay["outcome"] == "FAIL_CLOSED"
    assert replay["error_code"] == "REPLAY_DEPENDENCY_MISSING"


def test_exact_engine_runtime_invalid_object_and_digest_mismatch():
    runtime = PinnedEngineRuntimePort(ENGINE_CHECKOUT, handler)
    adapter = Engine2ObserverAdapter(runtime, InMemoryProjectionAudit([]))
    invalid = build(adapter, "req-invalid", "idem-invalid", "valid")
    object.__setattr__(invalid, "protocol_object", {"descriptor": None})
    assert adapter.observe(invalid)["engine_response"]["error_code"] == "PROTOCOL_OBJECT_INVALID"
    mismatch = build(adapter, "req-mismatch", "idem-mismatch", "valid")
    object.__setattr__(mismatch, "protocol_object_digest", "BAD")
    assert adapter.observe(mismatch)["engine_response"]["error_code"] == "EVIDENCE_DIGEST_MISMATCH"
