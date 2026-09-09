"""RFC 8785 safe-subset helpers compatible with frozen Engine 2."""

from __future__ import annotations

import hashlib
import json
from typing import Any


def canonical_json(value: Any) -> bytes:
    _validate(value)
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")


def sha256_digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest().upper()


def parse_json(source: str | bytes) -> Any:
    if isinstance(source, bytes):
        source = source.decode("utf-8")

    def reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key: {key}")
            result[key] = value
        return result

    value = json.loads(source, object_pairs_hook=reject_duplicates)
    _validate(value)
    return value


def _validate(value: Any) -> None:
    if value is None or isinstance(value, (str, bool, int)):
        return
    if isinstance(value, float):
        raise ValueError("floating-point values are outside the Engine 2 safe subset")
    if isinstance(value, list):
        for item in value:
            _validate(item)
        return
    if isinstance(value, dict):
        for key, item in value.items():
            if not isinstance(key, str):
                raise ValueError("contract object keys must be strings")
            _validate(item)
        return
    raise ValueError(f"unsupported contract value type: {type(value).__name__}")
