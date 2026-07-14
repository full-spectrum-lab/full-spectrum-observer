"""Engine v1.0.0 adapter: narrow local-first dict -> Observer envelope.

Engine v1.0.0 is the **regression baseline**. Its output is a narrow, local-
first contract dict with **no** Envelope / SubjectDeclaration /
EvaluationEvent / ReplayBundle concepts. This adapter projects that dict into the
unified Observer envelope, explicitly marking every v1.2-v1.5 section it does
not support as :data:`UNKNOWN` (never dropping, never zero-filling — PR-07 /
FR-PF-005). ``external_effect`` is always ``False`` (v1.0 never executes
business actions).
"""

from __future__ import annotations

from typing import Any, Dict, List

from .adapter_interface import AdaptationContext, IEngineAdapter
from .adapter_result import (
    AdapterResult,
    EngineEnvelope,
    ObserverEnvelope,
    UNKNOWN,
)

__all__ = ["EngineV1Adapter"]

# Sections that Engine v1.0.0 does not support; explicitly marked UNKNOWN.
_V1_UNSUPPORTED_SECTIONS = (
    "subject_declaration",
    "evaluation_events",
    "replay_bundle",
    "rbac",
    "desensitization",
    "review",
    "resilience",
    "connector",
    "hard_gate",
)


class EngineV1Adapter(IEngineAdapter):
    """Adapter for the pinned Engine v1.0.0 regression baseline."""

    source_engine_version = "1.0.0"

    def adapt(self, raw_output: Dict[str, Any], ctx: AdaptationContext) -> AdapterResult:
        raw = raw_output or {}

        # v1.0 has a narrow profile/gate shape; map what exists, else UNKNOWN.
        profile = raw.get("profile")
        scenario = raw.get("scenario")
        gate = raw.get("gate")
        if profile is not None or scenario is not None or gate is not None:
            profile_scenario: Any = {
                "profile": profile if profile is not None else UNKNOWN,
                "scenario_ref": scenario if scenario is not None else UNKNOWN,
                "gate": gate if gate is not None else UNKNOWN,
            }
        else:
            profile_scenario = UNKNOWN

        unknowns: List[str] = [s for s in _V1_UNSUPPORTED_SECTIONS]
        if profile_scenario is UNKNOWN:
            unknowns.append("profile_scenario")

        projected = ObserverEnvelope(
            source_version="1.0.0",
            profile_scenario=profile_scenario,
            subject_declaration=UNKNOWN,
            evaluation_events=UNKNOWN,
            replay_bundle=UNKNOWN,
            rbac=UNKNOWN,
            desensitization=UNKNOWN,
            review=UNKNOWN,
            resilience=UNKNOWN,
            connector=UNKNOWN,
            hard_gate=UNKNOWN,
            unknowns=unknowns,
            external_effect=False,
        ).finalize()

        raw_env = EngineEnvelope.build(envelope_version="raw-v1.0", payload=raw)

        return AdapterResult(
            source_version="1.0.0",
            digest=projected.canonical_digest,
            raw_envelope=raw_env,
            projected_envelope=projected,
            unknowns=unknowns,
            external_effect=False,
            event_refs=[],
            replay_refs=[],
            review_refs=[],
        )
