"""Dual Schema validator: separate *format* checks from *governance* semantics.

Per SRS FR-AN-004 and architecture design §8.1 / test plan §3, the validator
checks **format** only. UNKNOWN / insufficient-evidence are *governance*
semantics and are reported in :attr:`ValidationResult.unknowns`, **never** as
format errors.

The raw / canonical / output triple is preserved by the caller
(:class:`~compat.adapter_result.AdapterResult`); this validator merely confirms
each envelope is well-formed and enforces two hard redlines:

* ``external_effect`` MUST be ``False`` (R15);
* ``connector.write_enabled`` MUST be ``False`` (R07, default OFF).

Strictness contract (D5)
------------------------
Validation is **fail-closed** and never silently coerces malformed input:

* ``envelope_version`` MUST be a member of the supported version set; any other
  value (e.g. ``"evil-envelope"``) is rejected.
* ``evaluation_events`` MUST be a ``list``; strings / other types are rejected
  (no implicit conversion).
* ``unknowns`` MUST be ``list[str]``; a bare string such as ``"abc"`` MUST NOT
  be silently split into ``["a", "b", "c"]`` — it is rejected (or explicitly
  handled), never passed through after a silent transformation.

Failure surfaces as ``ValidationResult(ok=False, field_errors=[...])``; the
caller (the facade) converts that into a structured ``UnsupportedVersionError``
— it is never silently allowed.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import FrozenSet, List

from .adapter_result import EngineEnvelope, ObserverEnvelope, UNKNOWN

__all__ = ["ValidationResult", "SchemaValidator"]

# Envelope versions this validator is willing to accept (the supported set).
ENGINE_ENVELOPE_VERSIONS_SUPPORTED: FrozenSet[str] = frozenset({"raw-v1.0", "1.2"})
OBSERVER_ENVELOPE_VERSIONS_SUPPORTED: FrozenSet[str] = frozenset({"obs-1.0"})

# Observer sections that may legitimately carry the UNKNOWN sentinel.
_UNKNOWN_SECTIONS = (
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


@dataclass
class ValidationResult:
    """Result of a single schema validation pass."""

    ok: bool
    field_errors: List[str] = field(default_factory=list)
    unknowns: List[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "ok": self.ok,
            "field_errors": list(self.field_errors),
            "unknowns": list(self.unknowns),
        }


class SchemaValidator:
    """Validates the Engine (raw) and Observer (projected) envelopes."""

    def __init__(
        self,
        engine_schema_ref: str = "engine-v1.0-v1.5-envelope",
        observer_schema_ref: str = "obs-envelope@obs-1.0",
    ) -> None:
        self.engine_schema_ref = engine_schema_ref
        self.observer_schema_ref = observer_schema_ref

    def validate_engine(self, env: EngineEnvelope) -> ValidationResult:
        """Validate the raw Engine envelope (format only, strict)."""
        errors: List[str] = []
        if not isinstance(env, EngineEnvelope):
            errors.append("ENGINE_ENVELOPE_TYPE_INVALID")
            return ValidationResult(ok=False, field_errors=errors, unknowns=[])

        # envelope_version must be in the supported set (reject "evil-envelope").
        if env.envelope_version not in ENGINE_ENVELOPE_VERSIONS_SUPPORTED:
            errors.append("ENGINE_ENVELOPE_VERSION_UNSUPPORTED")

        if not isinstance(env.payload, dict):
            errors.append("ENGINE_PAYLOAD_NOT_DICT")
            return ValidationResult(ok=False, field_errors=errors, unknowns=[])

        payload = env.payload

        # evaluation_events + unknowns on the payload must be well-typed; never
        # coerce a string into a per-character list.
        if "evaluation_events" in payload and payload["evaluation_events"] is not UNKNOWN:
            if not isinstance(payload["evaluation_events"], list):
                errors.append("ENGINE_EVALUATION_EVENTS_TYPE_INVALID")
        if "unknowns" in payload and payload["unknowns"] is not None:
            unk = payload["unknowns"]
            if not isinstance(unk, list) or not all(isinstance(u, str) for u in unk):
                errors.append("ENGINE_UNKNOWNS_TYPE_INVALID")

        # v1.5 Envelope structural sanity (v1.0 raw has no such keys).
        if payload.get("envelope_version") == "1.2":
            for key in ("source_version", "profile_scenario"):
                if key not in payload:
                    errors.append(f"ENGINE_REQUIRED_MISSING:{key}")

        return ValidationResult(ok=not errors, field_errors=errors, unknowns=[])

    def validate_observer(self, env: ObserverEnvelope) -> ValidationResult:
        """Validate the projected Observer envelope (format + redlines, strict)."""
        errors: List[str] = []
        unknowns: List[str] = []

        if env.envelope_version not in OBSERVER_ENVELOPE_VERSIONS_SUPPORTED:
            errors.append("OBS_ENVELOPE_VERSION_UNSUPPORTED")
        if not env.source_version:
            errors.append("OBS_SOURCE_VERSION_MISSING")
        if not env.canonical_digest:
            errors.append("OBS_DIGEST_MISSING")

        # evaluation_events must be a list (or the UNKNOWN sentinel); a bare
        # string/other type is rejected, never coerced.
        if env.evaluation_events is not UNKNOWN and not isinstance(env.evaluation_events, list):
            errors.append("OBS_EVALUATION_EVENTS_TYPE_INVALID")

        # unknowns must be list[str]; a bare string such as "abc" is NOT silently
        # split into ["a", "b", "c"] — it is rejected (fail-closed).
        if not isinstance(env.unknowns, list):
            errors.append("OBS_UNKNOWNS_TYPE_INVALID")
        elif not all(isinstance(u, str) for u in env.unknowns):
            errors.append("OBS_UNKNOWNS_ELEMENT_TYPE_INVALID")

        if not isinstance(env.external_effect, bool):
            errors.append("OBS_EXTERNAL_EFFECT_TYPE_INVALID")
        elif env.external_effect is True:
            # Redline R15: the compat layer never executes business actions.
            errors.append("OBS_EXTERNAL_EFFECT_MUST_BE_FALSE")

        # Collect explicitly-UNKNOWN sections (governance semantics, not errors).
        for name in _UNKNOWN_SECTIONS:
            if getattr(env, name) == UNKNOWN:
                unknowns.append(name)

        # Connector write-back MUST be OFF (redline R07).
        connector = env.connector
        if isinstance(connector, dict):
            if connector.get("write_enabled") is True:
                errors.append("OBS_CONNECTOR_WRITE_ENABLED_MUST_BE_FALSE")
        elif connector != UNKNOWN:
            errors.append("OBS_CONNECTOR_TYPE_INVALID")

        return ValidationResult(ok=not errors, field_errors=errors, unknowns=unknowns)
