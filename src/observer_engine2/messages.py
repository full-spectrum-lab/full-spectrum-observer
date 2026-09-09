"""Observer-side immutable records for the frozen Engine 2 wire contract."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping


@dataclass(frozen=True)
class SnapshotBinding:
    engine_commit: str
    observer_commit: str
    kg_commit: str
    protocol_schema_version: str
    knowledge_snapshot_ref: str

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


@dataclass(frozen=True)
class EngineRetrievalRequest:
    request_id: str
    idempotency_key: str
    generation: str
    protocol_object: Mapping[str, Any]
    protocol_object_digest: str
    snapshot_binding: SnapshotBinding

    def to_dict(self) -> dict[str, Any]:
        value = asdict(self)
        value["protocol_object"] = dict(self.protocol_object)
        return value


@dataclass(frozen=True)
class ErrorResponse:
    request_id: str
    error_code: str
    message: str
    retryable: bool
    retry_preconditions: str
    correlation_id: str

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
