"""Load the Engine 2 runtime from an exact external Git checkout."""

from __future__ import annotations

import importlib
import subprocess
import sys
from dataclasses import asdict
from pathlib import Path
from typing import Any, Callable, Mapping

from .messages import EngineRetrievalRequest

PINNED_ENGINE_RUNTIME_COMMIT = "11c6f83593a327457940c6b5832aa03aa50713ee"


class PinnedEngineRuntimePort:
    """Bridge Observer DTOs to Engine-owned classes without vendoring Engine."""

    def __init__(self, checkout: str | Path, handler: Callable[[Any], tuple[str, str, str]]):
        self.checkout = Path(checkout).resolve()
        head = subprocess.run(
            ["git", "-C", str(self.checkout), "rev-parse", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        if head != PINNED_ENGINE_RUNTIME_COMMIT:
            raise ValueError(f"Engine checkout must be exactly {PINNED_ENGINE_RUNTIME_COMMIT}; got {head}")

        source = str(self.checkout / "src")
        sys.path.insert(0, source)
        try:
            messages = importlib.import_module("engine2.messages")
            engine = importlib.import_module("engine2.engine")
        finally:
            sys.path.remove(source)
        self._messages = messages
        self._runtime = engine.OfflineEngine(handler)

    def retrieve(self, request: EngineRetrievalRequest) -> Mapping[str, Any]:
        binding = self._messages.SnapshotBinding(**request.snapshot_binding.to_dict())
        engine_request = self._messages.EngineRetrievalRequest(
            request.request_id,
            request.idempotency_key,
            request.generation,
            dict(request.protocol_object),
            request.protocol_object_digest,
            binding,
        )
        return self._runtime.retrieve(engine_request).to_dict()

    def replay(self, **values: str) -> Mapping[str, Any]:
        request = self._messages.ReplayRequest(**values)
        return asdict(self._runtime.replay(request))
