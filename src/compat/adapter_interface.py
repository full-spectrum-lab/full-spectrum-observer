"""Adapter abstraction and the adaptation context.

The Compatibility Adapter layer is a *projection* layer: it converts Engine
output into the unified Observer envelope. It never re-implements Engine
governance algorithms (FSHI/Risk/ESS/Gate) — see architecture design §8 / §9.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import List

from .adapter_result import AdapterResult

__all__ = ["AdaptationContext", "IEngineAdapter"]


@dataclass(frozen=True)
class AdaptationContext:
    """Per-invocation context handed to an :class:`IEngineAdapter`.

    Attributes:
        observation_id: Stable identifier for the observation being adapted.
        source_version: The resolved Engine version (e.g. ``"1.5.0"``).
        scenario_ref: Optional scenario reference carried by the input.
        enabled_capabilities: Capabilities the resolved engine version supports.
    """

    observation_id: str
    source_version: str
    scenario_ref: str | None = None
    enabled_capabilities: List[str] = field(default_factory=list)


class IEngineAdapter(ABC):
    """Abstract Engine adapter. Application code depends only on this interface."""

    #: Concrete source engine version handled by the adapter (e.g. ``"1.0.0"``).
    source_engine_version: str = "0.0.0"

    @abstractmethod
    def adapt(self, raw_output: dict, ctx: AdaptationContext) -> AdapterResult:
        """Project *raw_output* (Engine output) into an :class:`AdapterResult`.

        Implementations MUST:
          * mark v1.0-unsupported sections explicitly as ``UNKNOWN`` (never drop);
          * keep ``external_effect`` ``False``;
          * pass EvaluationEvent/ReplayBundle/Review as *references* only
            (never merge their ownership into the Observation);
          * never treat ``ObservedSubject`` as an authentication principal.
        """
        raise NotImplementedError
