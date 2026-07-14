"""Compatibility matrix over the v1.2-v1.5 contract mapping.

Tracks, per capability, which Engine contract version introduces it, the
Observer field it maps to, and the fidelity rule that must hold. The matrix is
the runtime data source and the VG2 contract document backing
``docs/compat/contract-mapping-v1.2-v1.5.md``.

Authoritative contract口径 (ADR-001) — **there is no v1.1 Subject**:
v1.2 Envelope · v1.3 Profile/Scenario/UNKNOWN/hard-gate · v1.4
EvaluationEvent/ReplayBundle · v1.5 Subject/RBAC/Desensitization/Review/
Resilience/Connector.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from typing import List, Optional

__all__ = ["ContractMapping", "CompatibilityMatrix"]

_CONTRACT_MAP_PATH = os.path.join(
    os.path.dirname(__file__), "contract_map_v1.2_v1.5.json"
)


@dataclass(frozen=True)
class ContractMapping:
    """A single capability -> Engine contract -> Observer field mapping."""

    capability: str
    engine_contract_version: str
    observer_field: str
    fidelity_rule: str
    supported: bool

    @property
    def key(self) -> tuple:
        return (self.capability, self.engine_contract_version)

    def to_dict(self) -> dict:
        return {
            "capability": self.capability,
            "engine_contract_version": self.engine_contract_version,
            "observer_field": self.observer_field,
            "fidelity_rule": self.fidelity_rule,
            "supported": self.supported,
        }


class CompatibilityMatrix:
    """In-memory compatibility matrix with coverage / all-green judgement."""

    def __init__(self, mappings: List[ContractMapping]) -> None:
        self._mappings = list(mappings)
        self._index = {(m.capability, m.engine_contract_version): m for m in self._mappings}

    @classmethod
    def load(cls, path: str = _CONTRACT_MAP_PATH) -> "CompatibilityMatrix":
        """Load the matrix from the bundled JSON contract-mapping file."""
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        mappings = [ContractMapping(**item) for item in data["mappings"]]
        return cls(mappings)

    @classmethod
    def default(cls) -> "CompatibilityMatrix":
        """Load the canonical bundled contract-mapping matrix."""
        return cls.load(_CONTRACT_MAP_PATH)

    def lookup(
        self, capability: str, contract_version: str
    ) -> Optional[ContractMapping]:
        return self._index.get((capability, contract_version))

    def covers(self, capability: str, contract_version: str) -> bool:
        """Return ``True`` iff a *supported* mapping exists for the pair."""
        mapping = self._index.get((capability, contract_version))
        return mapping is not None and mapping.supported

    def all_green(self) -> bool:
        """Return ``True`` iff every mapping is supported (100% pass)."""
        return bool(self._mappings) and all(m.supported for m in self._mappings)

    def coverage(self) -> float:
        """Fraction of mappings that are supported (1.0 == 100%)."""
        if not self._mappings:
            return 0.0
        return sum(1 for m in self._mappings if m.supported) / len(self._mappings)

    def unsupported(self) -> List[ContractMapping]:
        return [m for m in self._mappings if not m.supported]

    @property
    def mappings(self) -> List[ContractMapping]:
        return list(self._mappings)
