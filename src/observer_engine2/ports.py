"""Ports owned by the Observer Engine 2 adapter."""

from __future__ import annotations

from typing import Any, Mapping, Protocol

from .messages import EngineRetrievalRequest


class Engine2Port(Protocol):
    def retrieve(self, request: EngineRetrievalRequest) -> Mapping[str, Any]: ...


class ProjectionAuditPort(Protocol):
    def append(self, record: Mapping[str, Any]) -> None: ...
