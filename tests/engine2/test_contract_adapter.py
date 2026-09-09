from __future__ import annotations

from copy import deepcopy

import pytest

from observer_engine2.adapter import Engine2ObserverAdapter, InMemoryProjectionAudit, ProjectionMutationError
from observer_engine2.canonical import canonical_json, parse_json, sha256_digest
from observer_engine2.messages import SnapshotBinding


class FakeEngine:
    def __init__(self, response):
        self.response = response
        self.requests = []

    def retrieve(self, request):
        self.requests.append(request.to_dict())
        return deepcopy(self.response(request) if callable(self.response) else self.response)


def binding():
    return SnapshotBinding("engine", "observer", "kg", "schema", "snapshot")


def result(request_id="req-1", decision="ALLOW", verification="VERIFIED", hard_gate="PASS"):
    semantic = {
        "request_id": request_id,
        "generation": "GEN2",
        "decision": decision,
        "verification_status": verification,
        "hard_gate": hard_gate,
    }
    digest = sha256_digest(semantic)
    return {**semantic, "result_digest": digest, "original_engine_result_digest": digest}


def error(request_id="req-1", code="PROTOCOL_OBJECT_INVALID", retryable=False):
    return {
        "request_id": request_id,
        "error_code": code,
        "message": "frozen error",
        "retryable": retryable,
        "retry_preconditions": "NONE",
        "correlation_id": "corr-1",
    }


def request(adapter, *, request_id="req-1", key="idem-1", payload=None):
    return adapter.build_request(
        request_id=request_id,
        idempotency_key=key,
        protocol_object=payload or {"descriptor": "offline"},
        snapshot_binding=binding(),
    )


@pytest.mark.parametrize(
    "decision,verification,gate",
    [("ALLOW", "VERIFIED", "PASS"), ("DENY", "VERIFIED", "FAIL"), ("UNKNOWN", "UNKNOWN", "UNKNOWN")],
)
def test_lossless_result_projection(decision, verification, gate):
    expected = result(decision=decision, verification=verification, hard_gate=gate)
    audit = InMemoryProjectionAudit([])
    adapter = Engine2ObserverAdapter(FakeEngine(expected), audit)
    projection = adapter.observe(request(adapter))
    assert projection["engine_response"] == expected
    assert audit.records == [projection]


def test_request_uses_exact_frozen_fields_and_digest():
    adapter = Engine2ObserverAdapter(FakeEngine(result()), InMemoryProjectionAudit([]))
    built = request(adapter, payload={"descriptor": "中文", "credential_status": "VALID"})
    assert set(built.to_dict()) == {
        "request_id", "idempotency_key", "generation", "protocol_object", "protocol_object_digest", "snapshot_binding"
    }
    assert set(built.snapshot_binding.to_dict()) == {
        "engine_commit", "observer_commit", "kg_commit", "protocol_schema_version", "knowledge_snapshot_ref"
    }
    assert built.protocol_object_digest == sha256_digest(dict(built.protocol_object))


def test_error_response_is_lossless_and_separate():
    expected = error(retryable=True)
    adapter = Engine2ObserverAdapter(FakeEngine(expected), InMemoryProjectionAudit([]))
    projection = adapter.observe(request(adapter))
    assert projection["engine_response_type"] == "ErrorResponse"
    assert projection["engine_response"] == expected


@pytest.mark.parametrize("field,replacement", [
    ("decision", "ALLOW"),
    ("verification_status", "PASS"),
    ("hard_gate", "PASS"),
    ("result_digest", "0" * 64),
    ("original_engine_result_digest", "F" * 64),
])
def test_result_rewrite_is_rejected(field, replacement):
    original = result(decision="UNKNOWN", verification="UNKNOWN", hard_gate="UNKNOWN")
    mutated = deepcopy(original)
    mutated[field] = replacement
    adapter = Engine2ObserverAdapter(FakeEngine(mutated), InMemoryProjectionAudit([]))
    with pytest.raises(ProjectionMutationError):
        adapter.observe(request(adapter))


@pytest.mark.parametrize("field,replacement", [
    ("error_code", "HIDDEN"), ("retryable", True), ("correlation_id", "")
])
def test_error_rewrite_is_detected_against_original(field, replacement):
    adapter = Engine2ObserverAdapter(FakeEngine(error()), InMemoryProjectionAudit([]))
    built = request(adapter)
    original = error()
    projection = adapter.project(built, original)
    projection["engine_response"][field] = replacement
    with pytest.raises(ProjectionMutationError):
        adapter.verify_projection(built, original, projection)


def test_snapshot_binding_rewrite_is_detected():
    adapter = Engine2ObserverAdapter(FakeEngine(result()), InMemoryProjectionAudit([]))
    built = request(adapter)
    projection = adapter.project(built, result())
    projection["request_snapshot_binding"]["kg_commit"] = "changed"
    with pytest.raises(ProjectionMutationError):
        adapter.verify_projection(built, result(), projection)


def test_audit_handoff_failure_is_fail_closed():
    class BrokenAudit:
        def append(self, record):
            raise OSError("offline sink unavailable")

    adapter = Engine2ObserverAdapter(FakeEngine(result()), BrokenAudit())
    projection = adapter.observe(request(adapter))
    assert projection["engine_response"]["error_code"] == "AUDIT_PERSISTENCE_FAILED"
    assert projection["engine_response"]["retryable"] is False


def test_canonical_safe_subset_and_duplicate_rejection():
    assert canonical_json({"z": "中文", "a": [True, 2]}) == b'{"a":[true,2],"z":"\xe4\xb8\xad\xe6\x96\x87"}'
    assert len(sha256_digest({"a": 1})) == 64 and sha256_digest({"a": 1}).upper() == sha256_digest({"a": 1})
    with pytest.raises(ValueError, match="duplicate"):
        parse_json('{"a":1,"a":2}')
    with pytest.raises(ValueError, match="floating-point"):
        canonical_json({"a": 1.5})
