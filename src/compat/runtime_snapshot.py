"""Runtime configuration snapshot with pinned (frozen) Engine baselines.

Per ADR-002 (C7) and architecture design §1.2 / §8.4, **every run fixes** the
Engine tag / commit / digest, the Adapter versions, the Schema references and
the fixture digests. The snapshot MUST NOT track ``main``, a floating branch or
``latest``. Any deviation fails :meth:`RuntimeConfigurationSnapshot.validate_self`.

Frozen Engine baselines (authoritative, override any design/README drift):

* Engine v1.0.0 — tag ``v1.0.0``, commit ``09062ba``.
* Engine v1.5.0 — tag ``v1.5.0``, commit ``28ac9ad``. The real digest is
  pending v1.5 sealing; the placeholder below is frozen and MUST be replaced
  with the real digest after v1.5 is sealed (tracked as an open item).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List

__all__ = ["RuntimeConfigurationSnapshot"]

# Frozen Engine v1.0.0 baseline (commit 09062ba is sealed).
ENGINE_V1_0_0_TAG = "v1.0.0"
ENGINE_V1_0_0_COMMIT = "09062ba"
ENGINE_V1_0_0_DIGEST = "sha256:frozen-09062ba-v1.0.0-baseline"  # frozen; replace with real v1.0.0 digest from Engine release manifest

# Frozen Engine v1.5.0 baseline (commit 28ac9ad, enterprise-pilot on v1.4).
ENGINE_V1_5_0_TAG = "v1.5.0"
ENGINE_V1_5_0_COMMIT = "28ac9ad"
# 待 v1.5 正式封板后回填真实 digest（当前为冻结占位值，非 latest/浮动）。
ENGINE_V1_5_0_DIGEST = "sha256:frozen-28ac9ad-v1.5.0-pending-real-digest"

# Tokens that are forbidden in any pinned field (floating / untracked).
_FORBIDDEN_TOKENS = frozenset({"", "latest", "main", "master", "HEAD", "floating", "None", None})


@dataclass(frozen=True)
class RuntimeConfigurationSnapshot:
    """A single run's fixed, reproducible Engine/Adapter/Schema/fixture config.

    Every field is a pinned, frozen value. :meth:`validate_self` rejects any
    floating/latest/empty value so that a run can never silently drift to a
    different Engine. This is the ADR-002 (C7) pinning guarantee.
    """

    engine_version_declared: str
    engine_tag: str
    engine_commit: str
    engine_digest: str
    adapter_versions: List[str] = field(default_factory=list)
    schema_refs: List[str] = field(default_factory=list)
    fixture_digests: List[str] = field(default_factory=list)

    def validate_self(self) -> bool:
        """Return ``True`` only if all pinned values are concrete (non-floating).

        Rejects ``latest`` / ``main`` / ``master`` / ``HEAD`` / ``floating`` /
        empty strings (and ``None``) in any pinned field, including per-fixture
        and per-schema references.
        """
        for token in (
            self.engine_version_declared,
            self.engine_tag,
            self.engine_commit,
            self.engine_digest,
        ):
            if token in _FORBIDDEN_TOKENS:
                return False
        for ref in self.adapter_versions:
            if ref in _FORBIDDEN_TOKENS:
                return False
        for ref in self.schema_refs:
            if ref in _FORBIDDEN_TOKENS:
                return False
        for ref in self.fixture_digests:
            if ref in _FORBIDDEN_TOKENS:
                return False
        return True

    @classmethod
    def frozen_v1_0_0(cls) -> "RuntimeConfigurationSnapshot":
        """Build the frozen snapshot for the regression baseline Engine v1.0.0."""
        return cls(
            engine_version_declared="1.0.0",
            engine_tag=ENGINE_V1_0_0_TAG,
            engine_commit=ENGINE_V1_0_0_COMMIT,
            engine_digest=ENGINE_V1_0_0_DIGEST,
            adapter_versions=["EngineV1Adapter@1.0.0"],
            schema_refs=["obs-envelope@obs-1.0", "engine-v1.0-raw@legacy"],
            fixture_digests=["sha256:frozen-v1.0_golden-regression"],
        )

    @classmethod
    def frozen_v1_5_0(cls) -> "RuntimeConfigurationSnapshot":
        """Build the frozen snapshot for the adaptation target Engine v1.5.0."""
        return cls(
            engine_version_declared="1.5.0",
            engine_tag=ENGINE_V1_5_0_TAG,
            engine_commit=ENGINE_V1_5_0_COMMIT,
            engine_digest=ENGINE_V1_5_0_DIGEST,
            adapter_versions=["EngineV15Adapter@1.5.0"],
            schema_refs=["obs-envelope@obs-1.0", "engine-v1.5-envelope@1.2"],
            fixture_digests=["sha256:frozen-v1.5_case005-closure"],
        )
