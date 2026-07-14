"""Adapter result model: dual-envelope storage and reference-only passing.

This module defines the data model produced by every adapter:

* :class:`EngineEnvelope`  — the *raw* Engine output (v1.0 dict or v1.5 Envelope).
* :class:`ObserverEnvelope` — the *projected* unified Observer envelope.
* :class:`AdapterResult`   — holds **both** envelopes (raw + projected dual
  attestation) plus the reference-only handles for Engine-sourced events and
  the explicit ``unknowns`` list.

The three references required by the dual-Schema contract (T04) are:

* **raw**      — :attr:`AdapterResult.raw_envelope` (Engine output, verbatim);
* **canonical**— the ``canonical_digest`` carried by both envelopes;
* **output**   — :attr:`AdapterResult.projected_envelope` (Observer projection).

The three ``*Ref`` classes implement the **reference-only** ownership rule
(architecture §8.7 / §9): EvaluationEvent / ReplayBundle / Review are passed by
reference and are **not** merged into Observation ownership.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Tuple

from .canonical import sha256_hex

__all__ = [
    "UNKNOWN",
    "EngineEnvelope",
    "ObserverEnvelope",
    "EvaluationEventRef",
    "ReplayBundleRef",
    "ReviewRef",
    "AdapterResult",
]

# Explicit sentinel marking a capability/field the source Engine does NOT
# support. Adapters MUST use this instead of silently dropping or zero-filling.
UNKNOWN = "UNKNOWN"


@dataclass(frozen=True)
class EngineEnvelope:
    """The raw Engine output, preserved verbatim for dual attestation."""

    envelope_version: str
    payload: Dict[str, Any]
    canonical_digest: str

    @classmethod
    def build(cls, envelope_version: str, payload: Dict[str, Any]) -> "EngineEnvelope":
        return cls(envelope_version, payload, sha256_hex(payload))

    def to_dict(self) -> Dict[str, Any]:
        return {
            "envelope_version": self.envelope_version,
            "payload": self.payload,
            "canonical_digest": self.canonical_digest,
        }


@dataclass(frozen=True)
class EvaluationEventRef:
    """Reference (not ownership) to an Engine EvaluationEvent."""

    event_id: str
    event_digest: str
    bundle_ref: str = ""

    def to_dict(self) -> Dict[str, Any]:
        return {
            "event_id": self.event_id,
            "event_digest": self.event_digest,
            "bundle_ref": self.bundle_ref,
        }


@dataclass(frozen=True)
class ReplayBundleRef:
    """Reference (not ownership) to an Engine ReplayBundle."""

    bundle_id: str
    capability_level: str
    missing_deps: Tuple[str, ...] = ()

    def to_dict(self) -> Dict[str, Any]:
        return {
            "bundle_id": self.bundle_id,
            "capability_level": self.capability_level,
            "missing_deps": list(self.missing_deps),
        }


@dataclass(frozen=True)
class ReviewRef:
    """Reference (not ownership) to an Engine ReviewRecord.

    ``original_event_ref`` MUST point to a real EvaluationEvent; a fabricated or
    empty reference is treated as evidence forgery (ownership rule R08).
    """

    review_id: str
    original_event_ref: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "review_id": self.review_id,
            "original_event_ref": self.original_event_ref,
        }


@dataclass
class ObserverEnvelope:
    """The unified Observer projection produced by an adapter.

    Unsupported sections are marked with the :data:`UNKNOWN` sentinel rather
    than dropped. ``external_effect`` is always ``False`` (the compat layer
    never executes business actions). ``connector`` write-back is always OFF.
    """

    envelope_version: str = "obs-1.0"
    source_version: str = ""
    canonical_digest: str = ""
    profile_scenario: Any = field(default_factory=dict)
    subject_declaration: Any = UNKNOWN
    evaluation_events: Any = UNKNOWN
    replay_bundle: Any = UNKNOWN
    rbac: Any = UNKNOWN
    desensitization: Any = UNKNOWN
    review: Any = UNKNOWN
    resilience: Any = UNKNOWN
    connector: Any = UNKNOWN
    hard_gate: Any = UNKNOWN
    unknowns: List[str] = field(default_factory=list)
    external_effect: bool = False

    # Field order used when computing the canonical digest (digest excluded to
    # avoid self-reference; it is assigned afterwards).
    _DIGEST_FIELDS: Tuple[str, ...] = (
        "envelope_version",
        "source_version",
        "profile_scenario",
        "subject_declaration",
        "evaluation_events",
        "replay_bundle",
        "rbac",
        "desensitization",
        "review",
        "resilience",
        "connector",
        "hard_gate",
        "unknowns",
        "external_effect",
    )

    def content_dict(self) -> Dict[str, Any]:
        """Return the digestable content (without ``canonical_digest``)."""
        return {f: getattr(self, f) for f in self._DIGEST_FIELDS}

    def compute_digest(self) -> str:
        """Compute and return the deterministic canonical digest of this envelope."""
        return sha256_hex(self.content_dict())

    def finalize(self) -> "ObserverEnvelope":
        """Assign :attr:`canonical_digest` from content and return self."""
        object.__setattr__(self, "canonical_digest", self.compute_digest())
        return self

    def to_dict(self, include_digest: bool = True) -> Dict[str, Any]:
        data = self.content_dict()
        if include_digest:
            data["canonical_digest"] = self.canonical_digest
        return data


@dataclass
class AdapterResult:
    """Outcome of an adaptation: raw + projected dual attestation plus refs."""

    source_version: str
    digest: str
    raw_envelope: EngineEnvelope
    projected_envelope: ObserverEnvelope
    unknowns: List[str] = field(default_factory=list)
    external_effect: bool = False
    event_refs: List[EvaluationEventRef] = field(default_factory=list)
    replay_refs: List[ReplayBundleRef] = field(default_factory=list)
    review_refs: List[ReviewRef] = field(default_factory=list)

    def verify_refs_resolvable(self) -> bool:
        """Return ``True`` iff every reference resolves to a real Engine entity.

        A reference is resolvable when it points at an entity that actually
        exists in the raw Engine output. A fabricated / empty
        ``original_event_ref`` therefore fails (ownership rule R08).
        """
        raw: Dict[str, Any] = self.raw_envelope.payload or {}
        events = raw.get("evaluation_events") or []
        valid_event_ids = {
            e.get("event_id") for e in events if isinstance(e, dict)
        }

        for ref in self.event_refs:
            if not ref.event_id or ref.event_id not in valid_event_ids:
                return False

        replay = raw.get("replay_bundle") or {}
        valid_bundle_ids: set = set()
        if isinstance(replay, dict) and replay.get("bundle_id"):
            valid_bundle_ids.add(replay.get("bundle_id"))
        for ref in self.replay_refs:
            if not ref.bundle_id or ref.bundle_id not in valid_bundle_ids:
                return False

        for ref in self.review_refs:
            if not ref.original_event_ref or ref.original_event_ref not in valid_event_ids:
                return False

        return True

    def to_dict(self) -> Dict[str, Any]:
        return {
            "source_version": self.source_version,
            "digest": self.digest,
            "external_effect": self.external_effect,
            "unknowns": list(self.unknowns),
            "raw_envelope": self.raw_envelope.to_dict(),
            "projected_envelope": self.projected_envelope.to_dict(),
            "event_refs": [r.to_dict() for r in self.event_refs],
            "replay_refs": [r.to_dict() for r in self.replay_refs],
            "review_refs": [r.to_dict() for r in self.review_refs],
        }
