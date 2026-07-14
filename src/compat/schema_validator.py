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
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List

from .adapter_result import EngineEnvelope, ObserverEnvelope, UNKNOWN

__all__ = ["ValidationResult", "SchemaValidator"]


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
        """Validate the raw Engine envelope (format only)."""
        errors: List[str] = []
        if not isinstance(env, EngineEnvelope):
            errors.append("ENGINE_ENVELOPE_TYPE_INVALID")
        if not getattr(env, "envelope_version", ""):
            errors.append("ENGINE_ENVELOPE_VERSION_MISSING")
        if not isinstance(env.payload, dict):
            errors.append("ENGINE_PAYLOAD_NOT_DICT")
        if not getattr(env, "canonical_digest", ""):
            errors.append("ENGINE_DIGEST_MISSING")

        # v1.5 Envelope structural sanity (v1.0 raw has no such keys).
        payload = env.payload if isinstance(env.payload, dict) else {}
        if payload.get("envelope_version") == "1.2":
            for key in ("source_version", "profile_scenario"):
                if key not in payload:
                    errors.append(f"ENGINE_REQUIRED_MISSING:{key}")

        return ValidationResult(ok=not errors, field_errors=errors, unknowns=[])

    def validate_observer(self, env: ObserverEnvelope) -> ValidationResult:
        """Validate the projected Observer envelope (format + redlines)."""
        errors: List[str] = []
        unknowns: List[str] = []

        if env.envelope_version != "obs-1.0":
            errors.append("OBS_ENVELOPE_VERSION_INVALID")
        if not env.source_version:
            errors.append("OBS_SOURCE_VERSION_MISSING")
        if not env.canonical_digest:
            errors.append("OBS_DIGEST_MISSING")

        if not isinstance(env.external_effect, bool):
            errors.append("OBS_EXTERNAL_EFFECT_TYPE_INVALID")
        elif env.external_effect is True:
            # Redline R15: the compat layer never executes business actions.
            errors.append("OBS_EXTERNAL_EFFECT_MUST_BE_FALSE")

        # Collect explicitly-UNKNOWN sections (governance semantics, not errors).
        for name in (
            "subject_declaration",
            "evaluation_events",
            "replay_bundle",
            "rbac",
            "desensitization",
            "review",
            "resilience",
            "connector",
            "hard_gate",
        ):
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
