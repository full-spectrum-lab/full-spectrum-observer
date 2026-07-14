"""Observer v0.2.0-alpha Compatibility Adapter layer.

Dual-baseline (Engine v1.0.0 regression / v1.5.0 adaptation) projection and
validation layer over Engine *output data*. This is a pure Python package that
does NOT depend on the .NET Engine build; it only projects and validates the
Engine's output contracts.

Key invariants (architecture design §8, ADR-001/002/004):
  * Contract口径: Envelope=v1.2, Profile/Scenario/UNKNOWN/hard-gate=v1.3,
    EvaluationEvent/ReplayBundle=v1.4, Subject/RBAC/Desens/Review/Resil/
    Connector=v1.5. There is NO "v1.1 Subject".
  * fail-closed: negotiation/adapter/schema/reference failures raise a
    structured ``UnsupportedVersionError``; no silent fallback.
  * pinned Engine: ``RuntimeConfigurationSnapshot`` freezes tag/commit/digest.
  * UNKNOWN / hard gate preserved, never downgraded.
  * EvaluationEvent/ReplayBundle/Review passed by reference only.
  * ``external_effect`` is always ``False``; Connector write-back is OFF.
"""

from __future__ import annotations

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
from .compatibility_matrix import CompatibilityMatrix, ContractMapping
from .engine_facade import DualAttestation, EngineFacade
from .engine_v1_adapter import EngineV1Adapter
from .engine_v15_adapter import EngineV15Adapter
from .engine_version import EngineVersion
from .runtime_snapshot import RuntimeConfigurationSnapshot
from .schema_validator import SchemaValidator, ValidationResult
from .version_resolver import EngineVersionResolver, UnsupportedVersionError

__version__ = "0.2.0-alpha.2"

__all__ = [
    "UNKNOWN",
    "AdaptationContext",
    "IEngineAdapter",
    "AdapterResult",
    "EngineEnvelope",
    "ObserverEnvelope",
    "EvaluationEventRef",
    "ReplayBundleRef",
    "ReviewRef",
    "CompatibilityMatrix",
    "ContractMapping",
    "DualAttestation",
    "EngineFacade",
    "EngineV1Adapter",
    "EngineV15Adapter",
    "EngineVersion",
    "RuntimeConfigurationSnapshot",
    "SchemaValidator",
    "ValidationResult",
    "EngineVersionResolver",
    "UnsupportedVersionError",
]
