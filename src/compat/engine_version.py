"""Engine version value object and capability catalogue.

Contract version口径 (authoritative, ADR-001):

* v1.2  Envelope
* v1.3  Profile / Scenario / UNKNOWN / hard gate
* v1.4  EvaluationEvent / ReplayBundle
* v1.5  Subject Declaration / Operator-Service RBAC / Desensitization /
        Review / Resilience / Connector

There is **no** ``v1.1 Subject`` contract. Any code or document labelling
``Subject Declaration`` as v1.1 is a version-drift bug (F1) and must be
corrected to v1.5.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import FrozenSet

__all__ = [
    "CAP_ENVELOPE",
    "CAP_PROFILE_SCENARIO",
    "CAP_UNKNOWN_HARD_GATE",
    "CAP_EVALUATION_EVENT",
    "CAP_REPLAY_BUNDLE",
    "CAP_SUBJECT_DECLARATION",
    "CAP_RBAC",
    "CAP_DESENSITIZATION",
    "CAP_REVIEW",
    "CAP_RESILIENCE",
    "CAP_CONNECTOR",
    "CAPABILITY_BY_CONTRACT",
    "EngineVersion",
]

# --- Capability identifiers (namespaced by authoritative contract version) ---
CAP_ENVELOPE = "v1.2:envelope"
CAP_PROFILE_SCENARIO = "v1.3:profile_scenario"
CAP_UNKNOWN_HARD_GATE = "v1.3:unknown_hard_gate"
CAP_EVALUATION_EVENT = "v1.4:evaluation_event"
CAP_REPLAY_BUNDLE = "v1.4:replay_bundle"
CAP_SUBJECT_DECLARATION = "v1.5:subject_declaration"
CAP_RBAC = "v1.5:rbac"
CAP_DESENSITIZATION = "v1.5:desensitization"
CAP_REVIEW = "v1.5:review"
CAP_RESILIENCE = "v1.5:resilience"
CAP_CONNECTOR = "v1.5:connector"

# All capabilities introduced cumulatively by Engine v1.5.
ALL_V1_5_CAPABILITIES: FrozenSet[str] = frozenset(
    {
        CAP_ENVELOPE,
        CAP_PROFILE_SCENARIO,
        CAP_UNKNOWN_HARD_GATE,
        CAP_EVALUATION_EVENT,
        CAP_REPLAY_BUNDLE,
        CAP_SUBJECT_DECLARATION,
        CAP_RBAC,
        CAP_DESENSITIZATION,
        CAP_REVIEW,
        CAP_RESILIENCE,
        CAP_CONNECTOR,
    }
)

# Engine v1.0.0 is the narrow, local-first contract baseline: it predates the
# v1.2-v1.5 capability set entirely, so it supports none of them.
ALL_V1_0_CAPABILITIES: FrozenSet[str] = frozenset()

CAPABILITY_BY_CONTRACT: dict[str, FrozenSet[str]] = {
    "v1.0": ALL_V1_0_CAPABILITIES,
    "v1.2": frozenset({CAP_ENVELOPE}),
    "v1.3": frozenset({CAP_PROFILE_SCENARIO, CAP_UNKNOWN_HARD_GATE}),
    "v1.4": frozenset({CAP_EVALUATION_EVENT, CAP_REPLAY_BUNDLE}),
    "v1.5": ALL_V1_5_CAPABILITIES,
}


@dataclass(frozen=True)
class EngineVersion:
    """Immutable value object describing a supported Engine baseline.

    Attributes:
        version: Concrete engine release, e.g. ``"1.0.0"`` or ``"1.5.0"``.
        contract_baseline: Authoritative contract baseline supported by this
            engine (``"v1.0"`` for the legacy narrow contract, ``"v1.5"`` for
            the enterprise-pilot baseline).
        capabilities: Frozen set of capability identifiers (see module
            constants) that this engine baseline supports.
    """

    version: str
    contract_baseline: str
    capabilities: FrozenSet[str] = field(default_factory=frozenset)

    def supports_capability(self, cap: str) -> bool:
        """Return ``True`` iff *cap* is supported by this engine version."""
        return cap in self.capabilities

    @staticmethod
    def from_version(version: str) -> "EngineVersion":
        """Build an :class:`EngineVersion` for a known baseline.

        Raises:
            ValueError: if *version* is not a recognized pinned baseline.
        """
        norm = version
        if norm in ("1.0.0", "1.0"):
            return EngineVersion("1.0.0", "v1.0", ALL_V1_0_CAPABILITIES)
        if norm in ("1.5.0", "1.5"):
            return EngineVersion("1.5.0", "v1.5", ALL_V1_5_CAPABILITIES)
        raise ValueError(f"Unsupported engine version baseline: {version!r}")
