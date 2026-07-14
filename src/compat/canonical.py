"""Deterministic canonical serialization and digest helpers.

These helpers guarantee that **identical inputs produce identical byte
sequences (and therefore identical digests) on Windows and Linux workers**,
which is a hard release requirement (XPLAT-W/L, DET-rerun). We deliberately
avoid any platform-specific ordering or formatting so that:

* the same fixture re-run yields the same ``canonical_digest`` (DET-rerun);
* serialization is stable across Windows/Linux Worker (XPLAT-W/L).

The Observer Compatibility Adapter layer is a pure projection/validation
layer over Engine *output data*; it never re-implements Engine governance
algorithms (FSHI/Risk/ESS/Gate). See architecture design §8 / §9.
"""

from __future__ import annotations

import hashlib
import json
from typing import Any

__all__ = ["canonical_json", "sha256_hex"]


def canonical_json(obj: Any) -> str:
    """Return a stable, sorted JSON representation of *obj*.

    Uses ``sort_keys=True`` and compact separators, with ``ensure_ascii=False``
    so that Unicode content is byte-stable regardless of the host platform.
    """
    return json.dumps(
        obj,
        sort_keys=True,
        ensure_ascii=False,
        separators=(",", ":"),
        default=_json_default,
    )


def _json_default(obj: Any) -> Any:
    """Fallback serializer for types that are not JSON-native."""
    if isinstance(obj, (set, frozenset)):
        return sorted(obj)
    if isinstance(obj, tuple):
        return list(obj)
    # dataclasses / objects with to_dict
    if hasattr(obj, "to_dict") and callable(obj.to_dict):
        return obj.to_dict()
    raise TypeError(f"Object of type {type(obj).__name__} is not JSON serializable.")


def sha256_hex(obj: Any) -> str:
    """Return the hex SHA-256 digest of the canonical JSON of *obj*."""
    payload = canonical_json(obj).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()
