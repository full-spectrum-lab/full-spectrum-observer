"""Build frozen requests and preserve Engine responses without reinterpretation."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from threading import RLock
from typing import Any, Mapping

from .canonical import sha256_digest
from .messages import EngineRetrievalRequest, ErrorResponse, SnapshotBinding
from .ports import Engine2Port, ProjectionAuditPort

RESULT_FIELDS = (
    "request_id",
    "generation",
    "decision",
    "verification_status",
    "result_digest",
    "hard_gate",
    "original_engine_result_digest",
)
ERROR_FIELDS = (
    "request_id",
    "error_code",
    "message",
    "retryable",
    "retry_preconditions",
    "correlation_id",
)


class ProjectionMutationError(ValueError):
    pass


@dataclass(frozen=True)
class InMemoryProjectionAudit:
    records: list[Mapping[str, Any]]

    def append(self, record: Mapping[str, Any]) -> None:
        self.records.append(deepcopy(dict(record)))


class Engine2ObserverAdapter:
    """Observer input -> frozen request -> Engine port -> lossless record."""

    def __init__(self, engine: Engine2Port, audit: ProjectionAuditPort):
        self._engine = engine
        self._audit = audit
        self._idempotency: dict[str, tuple[str, str]] = {}
        self._lock = RLock()

    @staticmethod
    def build_request(
        *,
        request_id: str,
        idempotency_key: str,
        protocol_object: Mapping[str, Any],
        snapshot_binding: SnapshotBinding,
    ) -> EngineRetrievalRequest:
        payload = deepcopy(dict(protocol_object))
        return EngineRetrievalRequest(
            request_id=request_id,
            idempotency_key=idempotency_key,
            generation="GEN2",
            protocol_object=payload,
            protocol_object_digest=sha256_digest(payload),
            snapshot_binding=snapshot_binding,
        )

    def observe(self, request: EngineRetrievalRequest) -> dict[str, Any]:
        request_before = request.to_dict()
        semantic_request_digest = sha256_digest({
            key: value for key, value in request_before.items() if key != "request_id"
        })
        response = deepcopy(dict(self._engine.retrieve(request)))
        if request.to_dict() != request_before:
            raise ProjectionMutationError("Engine port mutated the request binding")
        with self._lock:
            prior = self._idempotency.get(request.idempotency_key)
            allowed_response_request_id = request.request_id
            if prior and prior[0] == semantic_request_digest:
                allowed_response_request_id = prior[1]
            projection = self.project(request, response, allowed_response_request_id=allowed_response_request_id)
            if projection["engine_response_type"] == "EngineRetrievalResult" and prior is None:
                self._idempotency[request.idempotency_key] = (semantic_request_digest, response["request_id"])
        try:
            self._audit.append(projection)
        except Exception as exc:
            error = ErrorResponse(
                request.request_id,
                "AUDIT_PERSISTENCE_FAILED",
                "Observer projection audit handoff failed",
                False,
                "RETRY_ONLY_WHEN_NO_EXTERNAL_SIDE_EFFECT_AND_IDEMPOTENCY_IS_PROVEN",
                f"corr-{request.request_id}",
            )
            return self.project(request, error.to_dict())
        return projection

    @staticmethod
    def project(
        request: EngineRetrievalRequest,
        response: Mapping[str, Any],
        *,
        allowed_response_request_id: str | None = None,
    ) -> dict[str, Any]:
        wire = deepcopy(dict(response))
        if set(wire) == set(RESULT_FIELDS):
            Engine2ObserverAdapter._validate_result(
                request,
                wire,
                allowed_response_request_id=allowed_response_request_id or request.request_id,
            )
            response_type = "EngineRetrievalResult"
        elif set(wire) == set(ERROR_FIELDS):
            Engine2ObserverAdapter._validate_error(request, wire)
            response_type = "ErrorResponse"
        else:
            raise ProjectionMutationError("Engine response fields do not match a frozen message type")

        return {
            "type": "ObserverEngine2Projection",
            "request_snapshot_binding": request.snapshot_binding.to_dict(),
            "request_protocol_object_digest": request.protocol_object_digest,
            "engine_response_type": response_type,
            "engine_response": wire,
        }

    @staticmethod
    def verify_projection(
        request: EngineRetrievalRequest,
        original_response: Mapping[str, Any],
        projection: Mapping[str, Any],
    ) -> None:
        expected = Engine2ObserverAdapter.project(request, original_response)
        if dict(projection) != expected:
            raise ProjectionMutationError("Observer projection differs from the Engine response or request binding")

    @staticmethod
    def _validate_result(
        request: EngineRetrievalRequest,
        result: Mapping[str, Any],
        *,
        allowed_response_request_id: str,
    ) -> None:
        if result["request_id"] != allowed_response_request_id or result["generation"] != request.generation:
            raise ProjectionMutationError("Engine result does not match the request identity")
        if not all(isinstance(result[field], str) for field in RESULT_FIELDS):
            raise ProjectionMutationError("Engine result field types are invalid")
        semantic = {field: result[field] for field in RESULT_FIELDS if field not in {"result_digest", "original_engine_result_digest"}}
        digest = sha256_digest(semantic)
        if result["result_digest"] != digest or result["original_engine_result_digest"] != digest:
            raise ProjectionMutationError("Engine result digest was replaced or is internally inconsistent")

    @staticmethod
    def _validate_error(request: EngineRetrievalRequest, error: Mapping[str, Any]) -> None:
        if error["request_id"] != request.request_id:
            raise ProjectionMutationError("Engine error does not match the request identity")
        if not isinstance(error["retryable"], bool):
            raise ProjectionMutationError("Engine error retryable field is invalid")
        for field in ERROR_FIELDS:
            if field != "retryable" and not isinstance(error[field], str):
                raise ProjectionMutationError("Engine error field types are invalid")
        if not error["error_code"] or not error["correlation_id"]:
            raise ProjectionMutationError("Engine error code or correlation id was lost")
