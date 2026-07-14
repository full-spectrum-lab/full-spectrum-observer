"""Runtime configuration snapshot with pinned (frozen) Engine baselines.

Per ADR-002 (C7) and architecture design §1.2 / §8.4, **every run fixes** the
Engine tag / commit / digest, the Adapter versions, the Schema references and
the fixture digests. The snapshot MUST NOT track ``main``, a floating branch or
``latest``. Any deviation fails :meth:`RuntimeConfigurationSnapshot.validate_self`.

Frozen Engine baselines (authoritative, taken from the formally published
full-spectrum-engine releases):

* Engine v1.0.0 — tag ``v1.0.0``, commit
  ``09062bae2c7608bda79ee4bfde5779109e8e6197``,
  digest ``sha256:b38aabad7be19abf96acaeb7dd622a3c47eead1436e4cd274c3d862ff25dace6``.
* Engine v1.5.0 — tag ``v1.5.0``, commit
  ``f6eb92aee24a706f1b71dc073de6a760fca31092``,
  digest ``sha256:f1836bb56245c1f5cd7f6496aef504e1bdd3bb16b2255ee5af94ced215ac73cb``.

These are **real, verified** release anchors. The previously frozen
``pending-real-digest`` placeholder has been removed; the compat layer now
fails closed unless the declared Engine tag/commit/digest exactly match a
trusted release (see :data:`TRUSTED_ENGINE_RELEASES` and
:meth:`RuntimeConfigurationSnapshot.validate_engine_pin`).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List

__all__ = [
    "SYSTEM_VERSION",
    "TrustedEngineRelease",
    "TRUSTED_ENGINE_RELEASES",
    "TRUSTED_ENGINE_BY_TAG",
    "TRUSTED_ENGINE_BY_VERSION",
    "TRUSTED_ENGINE_TAGS",
    "RuntimeConfigurationSnapshot",
]

# Observer system version for this release (kept in sync with
# Directory.Build.props / baselines.lock.json / SOURCE_PACKAGE_MANIFEST.json).
SYSTEM_VERSION = "0.2.0-alpha.1"


@dataclass(frozen=True)
class TrustedEngineRelease:
    """An immutable, formally published Engine baseline.

    A release is only trustworthy when all three identity fields (``tag``,
    ``commit`` and ``digest``) match exactly. The compat layer treats any
    deviation as a forgery and fails closed.
    """

    tag: str
    version: str
    commit: str
    digest: str

    def matches(self, tag: str, commit: str, digest: str) -> bool:
        """Return ``True`` iff *all three* identity fields are equal."""
        return tag == self.tag and commit == self.commit and digest == self.digest


# ---------------------------------------------------------------------------
# Trusted Engine release manifest (authoritative, verified).
# Source: full-spectrum-engine v1.0.0 / v1.5.0 formal releases.
# ---------------------------------------------------------------------------
TRUSTED_ENGINE_RELEASES: tuple[TrustedEngineRelease, ...] = (
    TrustedEngineRelease(
        tag="v1.0.0",
        version="1.0.0",
        commit="09062bae2c7608bda79ee4bfde5779109e8e6197",
        digest="sha256:b38aabad7be19abf96acaeb7dd622a3c47eead1436e4cd274c3d862ff25dace6",
    ),
    TrustedEngineRelease(
        tag="v1.5.0",
        version="1.5.0",
        commit="f6eb92aee24a706f1b71dc073de6a760fca31092",
        digest="sha256:f1836bb56245c1f5cd7f6496aef504e1bdd3bb16b2255ee5af94ced215ac73cb",
    ),
)

TRUSTED_ENGINE_BY_TAG: dict[str, TrustedEngineRelease] = {
    r.tag: r for r in TRUSTED_ENGINE_RELEASES
}
TRUSTED_ENGINE_BY_VERSION: dict[str, TrustedEngineRelease] = {
    r.version: r for r in TRUSTED_ENGINE_RELEASES
}
TRUSTED_ENGINE_TAGS: frozenset[str] = frozenset(r.tag for r in TRUSTED_ENGINE_RELEASES)

# Frozen Engine v1.0.0 baseline (commit 09062bae… is sealed & published).
ENGINE_V1_0_0_TAG = "v1.0.0"
ENGINE_V1_0_0_COMMIT = "09062bae2c7608bda79ee4bfde5779109e8e6197"
ENGINE_V1_0_0_DIGEST = (
    "sha256:b38aabad7be19abf96acaeb7dd622a3c47eead1436e4cd274c3d862ff25dace6"
)

# Frozen Engine v1.5.0 baseline (commit f6eb92ae… is sealed & published).
ENGINE_V1_5_0_TAG = "v1.5.0"
ENGINE_V1_5_0_COMMIT = "f6eb92aee24a706f1b71dc073de6a760fca31092"
ENGINE_V1_5_0_DIGEST = (
    "sha256:f1836bb56245c1f5cd7f6496aef504e1bdd3bb16b2255ee5af94ced215ac73cb"
)

# Tokens that are forbidden in any pinned field (floating / untracked).
_FORBIDDEN_TOKENS = frozenset({"", "latest", "main", "master", "HEAD", "floating", "None", None})


@dataclass(frozen=True)
class RuntimeConfigurationSnapshot:
    """A single run's fixed, reproducible Engine/Adapter/Schema/fixture config.

    Every field is a pinned, frozen value. :meth:`validate_self` rejects any
    floating/latest/empty value so that a run can never silently drift to a
    different Engine. This is the ADR-002 (C7) pinning guarantee.

    Additionally, :meth:`validate_engine_pin` enforces that the declared
    Engine tag/commit/digest exactly match a trusted, published release —
    this is the fail-closed defence against forged / mismatched Engine
    configurations (D3).
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

    def engine_release(self) -> "TrustedEngineRelease | None":
        """Return the trusted release matching :attr:`engine_tag`, if any."""
        return TRUSTED_ENGINE_BY_TAG.get(self.engine_tag)

    def validate_engine_pin(self) -> List[str]:
        """Strictly verify the Engine identity against the trusted manifest.

        Returns a list of structured error codes (empty list == passed). The
        checks are:

        * ``ENGINE_TAG_UNTRUSTED``   — :attr:`engine_tag` is not a published tag.
        * ``ENGINE_VERSION_DECLARED_MISMATCH`` — the declared version does not
          agree with the trusted release's version.
        * ``ENGINE_COMMIT_MISMATCH`` — :attr:`engine_commit` != trusted commit.
        * ``ENGINE_DIGEST_MISMATCH`` — :attr:`engine_digest` != trusted digest.

        Any non-empty result means the Snapshot is forged / mismatched and must
        fail closed.
        """
        errors: List[str] = []
        release = TRUSTED_ENGINE_BY_TAG.get(self.engine_tag)
        if release is None:
            errors.append("ENGINE_TAG_UNTRUSTED")
            return errors
        if self.engine_version_declared != release.version:
            errors.append("ENGINE_VERSION_DECLARED_MISMATCH")
        if self.engine_commit != release.commit:
            errors.append("ENGINE_COMMIT_MISMATCH")
        if self.engine_digest != release.digest:
            errors.append("ENGINE_DIGEST_MISMATCH")
        return errors

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
