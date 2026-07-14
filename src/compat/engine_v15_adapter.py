"""Engine v1.5.0 adapter: v1.5 Envelope -> Observer envelope (faithful pass-through).

Engine v1.5.0 is the **adaptation target**. Its output embeds the v1.2 Envelope,
the v1.3 Profile/Scenario/UNKNOWN/hard-gate layer, the v1.4 EvaluationEvent/
ReplayBundle layer and the v1.5 Subject/RBAC/Desensitization/Review/Resilience/
Connector layer.

This adapter performs a **fidelity pass-through**: supported sections are copied
verbatim (UNKNOWN / digest / source_version / external_effect preserved). It does
**not** re-implement any Engine governance algorithm, and it does **not** treat
``ObservedSubject`` as an authentication principal (redline R06 / R01-identity).

Ownership rule (architecture §8.7 / §9): EvaluationEvent / ReplayBundle / Review
are passed by **reference only** via ``*Ref`` objects. Their content is retained
in the projected envelope for audit transparency, but the Observer does not take
ownership of the Engine events — ``original_event_ref`` must resolve to a real
event or the result is rejected (forgery, R08).
"""

from __future__ import annotations

from typing import Any, Dict, List, Tuple

from .adapter_interface import AdaptationContext, IEngineAdapter
from .adapter_result import (
    AdapterResult,
    EngineEnvelope,
    EvaluationEventRef,
    ObserverEnvelope,
    ReplayBundleRef,
    ReviewRef,
    UNKNOWN,
)

__all__ = ["EngineV15Adapter"]


class EngineV15Adapter(IEngineAdapter):
    """Adapter for the pinned Engine v1.5.0 adaptation target."""

    source_engine_version = "1.5.0"

    def adapt(self, raw_output: Dict[str, Any], ctx: AdaptationContext) -> AdapterResult:
        raw = raw_output or {}

        # Faithful pass-through of every supported section (no recomputation).
        profile_scenario = raw.get("profile_scenario", UNKNOWN)
        subject_declaration = raw.get("subject_declaration", UNKNOWN)
        evaluation_events = raw.get("evaluation_events", UNKNOWN)
        replay_bundle = raw.get("replay_bundle", UNKNOWN)
        rbac = raw.get("rbac", UNKNOWN)
        desensitization = raw.get("desensitization", UNKNOWN)
        review = raw.get("review", UNKNOWN)
        resilience = raw.get("resilience", UNKNOWN)
        connector = raw.get("connector", UNKNOWN)
        hard_gate = raw.get("hard_gate", UNKNOWN)

        # Preserve any UNKNOWN markers the Engine already declared. Only a real
        # list is accepted; a malformed value (e.g. a bare string "abc") is NOT
        # silently coerced into a per-character list — it is preserved as-is so
        # the downstream schema validator rejects it explicitly (D5, fail-closed).
        raw_unknowns = raw.get("unknowns")
        if isinstance(raw_unknowns, list):
            unknowns: List[str] = list(raw_unknowns)
        else:
            unknowns = raw_unknowns  # type: ignore[assignment]

        # Only append discovered UNKNOWN sections when we hold a real list.
        if isinstance(unknowns, list):
            for name, value in (
                ("profile_scenario", profile_scenario),
                ("subject_declaration", subject_declaration),
                ("evaluation_events", evaluation_events),
                ("replay_bundle", replay_bundle),
                ("rbac", rbac),
                ("desensitization", desensitization),
                ("review", review),
                ("resilience", resilience),
                ("connector", connector),
                ("hard_gate", hard_gate),
            ):
                if value == UNKNOWN and name not in unknowns:
                    unknowns.append(name)

        # external_effect is transparently passed through; Engine sets False.
        external_effect = bool(raw.get("external_effect", False))

        projected = ObserverEnvelope(
            source_version=ctx.source_version,
            profile_scenario=profile_scenario,
            subject_declaration=subject_declaration,
            evaluation_events=evaluation_events,
            replay_bundle=replay_bundle,
            rbac=rbac,
            desensitization=desensitization,
            review=review,
            resilience=resilience,
            connector=connector,
            hard_gate=hard_gate,
            unknowns=unknowns,
            external_effect=external_effect,
        ).finalize()

        raw_env = EngineEnvelope.build(
            envelope_version=raw.get("envelope_version", "1.2"),
            payload=raw,
        )

        # Reference-only handles (ownership stays with the Engine).
        event_refs = self._build_event_refs(raw, replay_bundle)
        replay_refs = self._build_replay_refs(replay_bundle)
        review_refs = self._build_review_refs(review)

        return AdapterResult(
            source_version=ctx.source_version,
            digest=projected.canonical_digest,
            raw_envelope=raw_env,
            projected_envelope=projected,
            unknowns=unknowns,
            external_effect=external_effect,
            event_refs=event_refs,
            replay_refs=replay_refs,
            review_refs=review_refs,
        )

    @staticmethod
    def _build_event_refs(
        raw: Dict[str, Any], replay_bundle: Any
    ) -> List[EvaluationEventRef]:
        bundle_id = (
            replay_bundle.get("bundle_id", "")
            if isinstance(replay_bundle, dict)
            else ""
        )
        refs: List[EvaluationEventRef] = []
        for event in raw.get("evaluation_events") or []:
            if not isinstance(event, dict):
                continue
            refs.append(
                EvaluationEventRef(
                    event_id=event.get("event_id", ""),
                    event_digest=event.get("event_digest", ""),
                    bundle_ref=bundle_id,
                )
            )
        return refs

    @staticmethod
    def _build_replay_refs(replay_bundle: Any) -> List[ReplayBundleRef]:
        if not isinstance(replay_bundle, dict) or not replay_bundle:
            return []
        return [
            ReplayBundleRef(
                bundle_id=replay_bundle.get("bundle_id", ""),
                capability_level=replay_bundle.get("capability_level", ""),
                missing_deps=tuple(replay_bundle.get("missing_deps") or []),
            )
        ]

    @staticmethod
    def _build_review_refs(review: Any) -> List[ReviewRef]:
        if not isinstance(review, dict) or not review:
            return []
        return [
            ReviewRef(
                review_id=review.get("review_id", ""),
                original_event_ref=review.get("original_event_ref", ""),
            )
        ]
