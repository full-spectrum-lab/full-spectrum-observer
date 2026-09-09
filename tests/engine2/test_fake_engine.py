from __future__ import annotations

from copy import deepcopy

from observer_engine2.adapter import Engine2ObserverAdapter, InMemoryProjectionAudit
from observer_engine2.canonical import sha256_digest
from observer_engine2.messages import SnapshotBinding


class StatefulFakeEngine:
    def __init__(self):
        self.seen = {}
        self.side_effects = 0

    def retrieve(self, request):
        payload_digest = sha256_digest({
            "generation": request.generation,
            "protocol_object": dict(request.protocol_object),
            "protocol_object_digest": request.protocol_object_digest,
            "snapshot_binding": request.snapshot_binding.to_dict(),
        })
        prior = self.seen.get(request.idempotency_key)
        if prior and prior[0] != payload_digest:
            return self._error(request.request_id, "IDEMPOTENCY_CONFLICT")
        if prior:
            return deepcopy(prior[1])
        if not isinstance(request.protocol_object.get("descriptor"), str):
            return self._error(request.request_id, "PROTOCOL_OBJECT_INVALID")
        if request.protocol_object_digest != sha256_digest(dict(request.protocol_object)):
            return self._error(request.request_id, "EVIDENCE_DIGEST_MISMATCH")
        response = self._result(request.request_id)
        self.side_effects += 1
        self.seen[request.idempotency_key] = (payload_digest, response)
        return deepcopy(response)

    @staticmethod
    def _result(request_id):
        semantic = {"request_id": request_id, "generation": "GEN2", "decision": "ALLOW", "verification_status": "FAKE_ONLY", "hard_gate": "PASS"}
        digest = sha256_digest(semantic)
        return {**semantic, "result_digest": digest, "original_engine_result_digest": digest}

    @staticmethod
    def _error(request_id, code):
        return {"request_id": request_id, "error_code": code, "message": code, "retryable": False, "retry_preconditions": "NONE", "correlation_id": f"corr-{request_id}"}


def make(adapter, request_id, key, descriptor):
    return adapter.build_request(
        request_id=request_id,
        idempotency_key=key,
        protocol_object={"descriptor": descriptor},
        snapshot_binding=SnapshotBinding("engine", "observer", "kg", "schema", "snapshot"),
    )


def test_same_idempotency_key_same_payload_has_one_side_effect():
    engine = StatefulFakeEngine()
    adapter = Engine2ObserverAdapter(engine, InMemoryProjectionAudit([]))
    first = adapter.observe(make(adapter, "req-1", "idem", "same"))
    second = adapter.observe(make(adapter, "req-2", "idem", "same"))
    assert first["engine_response"] == second["engine_response"]
    assert engine.side_effects == 1


def test_same_idempotency_key_conflicting_payload_fails_closed():
    engine = StatefulFakeEngine()
    adapter = Engine2ObserverAdapter(engine, InMemoryProjectionAudit([]))
    adapter.observe(make(adapter, "req-1", "idem", "first"))
    conflict = adapter.observe(make(adapter, "req-2", "idem", "second"))
    assert conflict["engine_response"]["error_code"] == "IDEMPOTENCY_CONFLICT"


def test_invalid_protocol_object_and_digest_mismatch_are_preserved():
    engine = StatefulFakeEngine()
    adapter = Engine2ObserverAdapter(engine, InMemoryProjectionAudit([]))
    invalid = make(adapter, "req-1", "invalid", "value")
    object.__setattr__(invalid, "protocol_object", {"descriptor": None})
    assert adapter.observe(invalid)["engine_response"]["error_code"] == "PROTOCOL_OBJECT_INVALID"
    mismatch = make(adapter, "req-2", "mismatch", "value")
    object.__setattr__(mismatch, "protocol_object_digest", "BAD")
    assert adapter.observe(mismatch)["engine_response"]["error_code"] == "EVIDENCE_DIGEST_MISMATCH"
