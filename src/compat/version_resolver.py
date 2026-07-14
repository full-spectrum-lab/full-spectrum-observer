"""Version negotiation (explicit support matrix) and the fail-closed error contract.

ADR-002 (F7/C7) mandates that version negotiation failures surface as a
*structured* :class:`UnsupportedVersionError` and that the facade fails closed.
There is **no** silent fallback to "the latest version" and **no** default
substitution: if a version is not in the explicit support matrix, the request
is rejected outright.

The reason codes below intentionally mirror the vocabulary of the Observer
Foundation reason-code catalogue (``FoundationReasonCodes.cs``) so that the
Python compat layer and the .NET host speak the same error language.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Dict

from .engine_version import EngineVersion
from .runtime_snapshot import RuntimeConfigurationSnapshot

__all__ = ["UnsupportedVersionError", "EngineVersionResolver"]

# Reason codes (aligned with FoundationReasonCodes.cs where a counterpart exists).
REASON_VERSION_UNSUPPORTED = "ENGINE_VERSION_MISMATCH"
REASON_SNAPSHOT_FLOATING = "SNAPSHOT_FLOATING_VERSION_FORBIDDEN"
REASON_SNAPSHOT_PIN_MISMATCH = "ENGINE_PIN_MISMATCH"
REASON_ADAPTER_MISSING = "ADAPTER_CASE_NOT_FOUND"
REASON_ADAPTER_VERSION_BINDING = "ADAPTER_VERSION_BINDING_MISMATCH"
REASON_SCHEMA_ENGINE_INVALID = "SCHEMA_REQUIRED_MISSING"
REASON_SCHEMA_OBSERVER_INVALID = "ADAPTER_OUTPUT_INVALID"
REASON_REFERENCE_UNRESOLVABLE = "SNAPSHOT_REFERENCE_MISSING"
REASON_DEPENDENCY_NOT_REPLAYABLE = "COMPAT_DEPENDENCY_NOT_REPLAYABLE"
REASON_INPUT_TYPE_INVALID = "INPUT_TYPE_INVALID"


@dataclass
class UnsupportedVersionError(Exception):
    """Structured, fail-closed error for unsupported Engine versions / negotiation.

    Attributes:
        requested_version: The engine version that was requested / declared.
        reason_code: Machine-readable reason (see module ``REASON_*`` constants).
        message: Human-readable explanation.
    """

    requested_version: str
    reason_code: str
    message: str

    def __post_init__(self) -> None:
        # Make this a proper Exception so it can be raised directly.
        super().__init__(self.message)

    def to_envelope(self) -> Dict[str, object]:
        """Return a structured error envelope (fail-closed, never claims success)."""
        return {
            "error": True,
            "kind": "UnsupportedVersionError",
            "requested_version": self.requested_version,
            "reason_code": self.reason_code,
            "message": self.message,
            "external_effect": False,
        }


class EngineVersionResolver:
    """Negotiates the target Engine version against an explicit support matrix.

    Only versions present in the support matrix are accepted. Resolution reads
    the declared version from a :class:`RuntimeConfigurationSnapshot`, verifies
    the snapshot is pinned (no floating values), and — beyond the explicit
    support matrix — strictly verifies the declared Engine tag/commit/digest
    against the trusted Engine release manifest (fail-closed, D3).
    """

    def __init__(self, supported: Dict[str, EngineVersion] | None = None) -> None:
        self._supported = dict(supported) if supported else self._default_matrix()

    @staticmethod
    def _default_matrix() -> Dict[str, EngineVersion]:
        return {
            "1.0.0": EngineVersion.from_version("1.0.0"),
            "1.5.0": EngineVersion.from_version("1.5.0"),
        }

    @classmethod
    def default(cls) -> "EngineVersionResolver":
        """Create a resolver with the canonical dual-baseline support matrix."""
        return cls(cls._default_matrix())

    @staticmethod
    def _normalize(version: str) -> str:
        mapping = {
            "1.0": "1.0.0",
            "1.0.0": "1.0.0",
            "1.5": "1.5.0",
            "1.5.0": "1.5.0",
        }
        return mapping.get(version, version)

    def is_supported(self, version: str) -> bool:
        """Return ``True`` iff *version* is in the explicit support matrix."""
        return self._normalize(version) in self._supported

    def fail_unsupported(
        self, version: str, reason_code: str, message: str
    ) -> UnsupportedVersionError:
        """Factory for a structured :class:`UnsupportedVersionError`."""
        return UnsupportedVersionError(version, reason_code, message)

    def resolve(self, snapshot) -> EngineVersion:
        """Resolve *snapshot* to a concrete :class:`EngineVersion`.

        Raises:
            UnsupportedVersionError: if *snapshot* is not a
                :class:`RuntimeConfigurationSnapshot` (D6); the snapshot floats
                (ADR-002 C7); the declared version is outside the explicit
                support matrix; or the declared Engine tag/commit/digest do not
                exactly match the trusted release manifest (D3, fail-closed).
        """
        # D6: every contract entry point returns a structured error for
        # unexpected input types instead of leaking AttributeError/TypeError.
        if not isinstance(snapshot, RuntimeConfigurationSnapshot):
            raise self.fail_unsupported(
                "<non-snapshot>",
                REASON_INPUT_TYPE_INVALID,
                "Version negotiation requires a RuntimeConfigurationSnapshot; "
                f"got {type(snapshot).__name__}.",
            )

        if not snapshot.validate_self():
            raise self.fail_unsupported(
                snapshot.engine_version_declared,
                REASON_SNAPSHOT_FLOATING,
                "RuntimeConfigurationSnapshot contains floating/latest/empty pinned "
                "values; a pinned Engine baseline is mandatory (ADR-002 C7).",
            )

        normalized = self._normalize(snapshot.engine_version_declared)
        resolved = self._supported.get(normalized)
        if resolved is None:
            raise self.fail_unsupported(
                snapshot.engine_version_declared,
                REASON_VERSION_UNSUPPORTED,
                f"Engine version {snapshot.engine_version_declared!r} is not in the "
                "explicit support matrix; no silent fallback is performed.",
            )

        # D3: strictly verify the declared Engine identity against the trusted
        # release manifest. Forged / mismatched tag-commit-digest triples are
        # rejected outright (fail-closed).
        pin_errors = snapshot.validate_engine_pin()
        if pin_errors:
            raise self.fail_unsupported(
                snapshot.engine_version_declared,
                REASON_SNAPSHOT_PIN_MISMATCH,
                "Engine baseline pin failed strict verification against the trusted "
                f"Engine release manifest: {', '.join(pin_errors)}.",
            )

        return resolved
