"""EngineFacade: orchestrates the dual-baseline compatibility pipeline.

Pipeline (per architecture design §5):
    RuntimeConfigurationSnapshot
      -> EngineVersionResolver.resolve   (version negotiation, fail-closed)
      -> EngineV1Adapter / EngineV15Adapter.adapt  (projection)
      -> SchemaValidator (engine + observer dual validation)
      -> AdapterResult.verify_refs_resolvable (reference resolution)
      -> DualAttestation (raw + projected dual storage proof)

Every failure is **fail-closed**: the facade raises a structured
:class:`UnsupportedVersionError` (no silent fallback, no half-written result,
no version downgrade). This is the ADR-002 (C7) behavioural guarantee.

Version/contract binding (D4): an adapter is bound to exactly one Engine
version (``EngineV1Adapter`` -> ``1.0.0``, ``EngineV15Adapter`` -> ``1.5.0``).
Registering an adapter under the wrong version, or dispatching to an adapter
whose bound version disagrees with the resolved contract, fails closed.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

from .adapter_interface import AdaptationContext, IEngineAdapter
from .adapter_result import AdapterResult
from .compatibility_matrix import CompatibilityMatrix
from .runtime_snapshot import RuntimeConfigurationSnapshot
from .schema_validator import SchemaValidator
from .version_resolver import (
    REASON_ADAPTER_MISSING,
    REASON_ADAPTER_VERSION_BINDING,
    REASON_DEPENDENCY_NOT_REPLAYABLE,
    REASON_INPUT_TYPE_INVALID,
    REASON_REFERENCE_UNRESOLVABLE,
    REASON_SCHEMA_ENGINE_INVALID,
    REASON_SCHEMA_OBSERVER_INVALID,
    EngineVersionResolver,
    UnsupportedVersionError,
)

__all__ = ["DualAttestation", "EngineFacade"]


@dataclass(frozen=True)
class DualAttestation:
    """Raw + projected dual storage proof for one adaptation run."""

    source_version: str
    raw_digest: str
    projected_digest: str
    external_effect: bool


class EngineFacade:
    """Front-door for adapting a pinned Engine baseline's output to Observer."""

    def __init__(
        self,
        resolver: Optional[EngineVersionResolver] = None,
        matrix: Optional[CompatibilityMatrix] = None,
        validator: Optional[SchemaValidator] = None,
    ) -> None:
        self._resolver = resolver or EngineVersionResolver.default()
        self._matrix = matrix or CompatibilityMatrix.default()
        self._validator = validator or SchemaValidator()
        self._adapters: Dict[str, IEngineAdapter] = {}
        self._attestations: List[DualAttestation] = []

    def register_adapter(self, version: str, adapter: IEngineAdapter) -> None:
        """Register an adapter for a concrete engine version (e.g. ``"1.5.0"``).

        Enforces strict version binding (D4): an adapter may only be
        registered for the Engine version it is built to serve. Registering
        ``EngineV1Adapter`` (bound to ``1.0.0``) under ``"1.5.0"`` — or any
        mismatch — fails closed with a structured error instead of silently
        allowing a wrong-version projection to pass.
        """
        if not isinstance(adapter, IEngineAdapter):
            raise UnsupportedVersionError(
                version,
                REASON_INPUT_TYPE_INVALID,
                f"adapter must implement IEngineAdapter; got {type(adapter).__name__}.",
            )
        if adapter.source_engine_version != version:
            raise UnsupportedVersionError(
                version,
                REASON_ADAPTER_VERSION_BINDING,
                f"Adapter {type(adapter).__name__} is bound to Engine "
                f"{adapter.source_engine_version} but was registered for {version}; "
                "version binding is strict (fail-closed).",
            )
        self._adapters[version] = adapter

    @property
    def attestations(self) -> List[DualAttestation]:
        return list(self._attestations)

    def execute(
        self,
        snapshot: RuntimeConfigurationSnapshot,
        raw_output: dict,
    ) -> AdapterResult:
        """Run the full adaptation pipeline and return a dual-attested result.

        Raises:
            UnsupportedVersionError: on any negotiation / adapter / schema /
                reference-resolution failure, or on unexpected input types
                (D6, fail-closed).
        """
        # D6: entry-point type guards — never leak AttributeError/TypeError.
        if not isinstance(snapshot, RuntimeConfigurationSnapshot):
            raise UnsupportedVersionError(
                "<non-snapshot>",
                REASON_INPUT_TYPE_INVALID,
                f"execute requires a RuntimeConfigurationSnapshot; "
                f"got {type(snapshot).__name__}.",
            )
        if not isinstance(raw_output, dict):
            raise UnsupportedVersionError(
                str(getattr(snapshot, "engine_version_declared", "?")),
                REASON_INPUT_TYPE_INVALID,
                f"raw_output must be a dict; got {type(raw_output).__name__}.",
            )

        # 1) Version negotiation (also enforces pinned/frozen snapshot + D3 pin).
        try:
            version = self._resolver.resolve(snapshot)
        except UnsupportedVersionError:
            raise  # fail-closed: propagate the structured error as-is.

        # 2) Adapter dispatch — missing adapter is a hard failure.
        adapter = self._adapters.get(version.version)
        if adapter is None:
            raise self._resolver.fail_unsupported(
                version.version,
                REASON_ADAPTER_MISSING,
                f"No adapter registered for engine version {version.version!r}.",
            )

        # D4: contract -> adapter consistency (defence in depth). The adapter's
        # bound version must agree with the resolved Engine contract; a
        # misregistered adapter is rejected even if somehow present.
        if adapter.source_engine_version != version.version:
            raise self._resolver.fail_unsupported(
                version.version,
                REASON_ADAPTER_VERSION_BINDING,
                f"Adapter {type(adapter).__name__} is bound to Engine "
                f"{adapter.source_engine_version}, which disagrees with the resolved "
                f"contract {version.version!r} (fail-closed).",
            )

        # 3) Adaptation (projection + reference extraction).
        scenario_ref = None
        ps = raw_output.get("profile_scenario")
        if isinstance(ps, dict):
            scenario_ref = ps.get("scenario_ref")
        elif isinstance(raw_output.get("scenario"), str):
            scenario_ref = raw_output.get("scenario")
        ctx = AdaptationContext(
            observation_id=str(raw_output.get("observation_id", "") or f"obs-{version.version}"),
            source_version=version.version,
            scenario_ref=scenario_ref,
            enabled_capabilities=list(version.capabilities),
        )
        result = adapter.adapt(raw_output, ctx)

        # 4) Dual Schema validation (format only; UNKNOWN is not an error).
        engine_check = self._validator.validate_engine(result.raw_envelope)
        observer_check = self._validator.validate_observer(result.projected_envelope)
        if not engine_check.ok:
            raise UnsupportedVersionError(
                version.version,
                REASON_SCHEMA_ENGINE_INVALID,
                "; ".join(engine_check.field_errors),
            )
        if not observer_check.ok:
            raise UnsupportedVersionError(
                version.version,
                REASON_SCHEMA_OBSERVER_INVALID,
                "; ".join(observer_check.field_errors),
            )

        # 5) Reference resolution — fabricated / missing refs fail closed.
        if not result.verify_refs_resolvable():
            raise self._resolver.fail_unsupported(
                version.version,
                REASON_REFERENCE_UNRESOLVABLE,
                "EvaluationEvent/ReplayBundle/Review reference is not resolvable "
                "(possibly forged or dependency not replayable).",
            )

        # 6) Dual attestation (raw + projected) — both digests retained.
        self._attestations.append(
            DualAttestation(
                source_version=version.version,
                raw_digest=result.raw_envelope.canonical_digest,
                projected_digest=result.projected_envelope.canonical_digest,
                external_effect=result.external_effect,
            )
        )
        return result
