"""INDEPENDENT QA reproduction of the three third-party re-verification FAIL
cases (report FS-OBS-TR-020A1-EXT-20260714) for Full Spectrum Observer
v0.2.0-alpha.2.

This script does NOT import the engineer's test module. It reproduces the
*verbatim* negative / boundary inputs from the independent re-verification
report and asserts the fail-closed behaviours now hold. It is run directly
with the managed venv Python, separate from pytest, to prove the fixes are
real and not "fake green".

Closed cases (previously open in the third-party re-verification):
  * P1 (F-04)  — three-way version binding. A raw Envelope that self-declares a
                 conflicting ``source_version`` MUST be rejected, and a
                 declaration-less / matching raw MUST project the *trusted*
                 resolved version (no conflicting identity).
  * P2-A (F-08)— an empty ``event_digest`` in ``evaluation_events`` MUST be
                 rejected (reference unresolvable).
  * P2-B (F-08)— an empty ``review_id`` MUST be rejected.

Exit code 0 => all independent reproductions PASS. Non-zero => at least one FAIL.
"""

from __future__ import annotations

import os
import sys

# Make the standalone src/compat package importable (mirrors the repo conftest).
_REPO = os.path.abspath(os.path.dirname(__file__))
_SRC = os.path.join(_REPO, "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from compat.adapter_interface import AdaptationContext  # noqa: E402
from compat.engine_facade import EngineFacade  # noqa: E402
from compat.engine_v15_adapter import EngineV15Adapter  # noqa: E402
from compat.runtime_snapshot import RuntimeConfigurationSnapshot  # noqa: E402
from compat.version_resolver import (  # noqa: E402
    REASON_REFERENCE_UNRESOLVABLE,
    REASON_VERSION_UNSUPPORTED,
    UnsupportedVersionError,
)

# Literal anchor from the third-party report (real published Engine v1.5.0).
ENGINE_V1_5_0_TAG = "v1.5.0"
ENGINE_V1_5_0_COMMIT = "f6eb92aee24a706f1b71dc073de6a760fca31092"
ENGINE_V1_5_0_DIGEST = (
    "sha256:f1836bb56245c1f5cd7f6496aef504e1bdd3bb16b2255ee5af94ced215ac73cb"
)

RESULTS = []


def check(name: str, ok: bool, detail: str = "") -> None:
    RESULTS.append((name, ok, detail))
    status = "PASS" if ok else "FAIL"
    print(f"[{status}] {name}  {detail}")


def _snapshot_v1_5() -> RuntimeConfigurationSnapshot:
    """Literal frozen snapshot: declare 1.5.0 bound to the real Engine anchor."""
    return RuntimeConfigurationSnapshot(
        engine_version_declared="1.5.0",
        engine_tag=ENGINE_V1_5_0_TAG,
        engine_commit=ENGINE_V1_5_0_COMMIT,
        engine_digest=ENGINE_V1_5_0_DIGEST,
        adapter_versions=["EngineV15Adapter@1.5.0"],
        schema_refs=["obs-envelope@obs-1.0", "engine-v1.5-envelope@1.2"],
        fixture_digests=["sha256:frozen-v1.5_case005-closure"],
    )


def _base_raw(source_version: str | None = "1.5.0") -> dict:
    """Minimal faithful v1.5 raw output with resolvable references.

    ``source_version`` is omitted when ``None`` so the P1 Gate trusts the
    resolved contract (declaration-less legacy payload, no false rejection).
    """
    raw: dict = {
        "envelope_version": "1.5",
        "observation_id": "obs-a2-001",
        "profile_scenario": {"scenario_ref": "s-a2"},
        "evaluation_events": [
            {
                "event_id": "evt-005-a",
                "event_digest": "sha256:evt-005-a",
                "capability": "v1.4:evaluation_event",
                "outcome": "logged",
            }
        ],
        "replay_bundle": {
            "bundle_id": "rb-005",
            "capability_level": "L3",
            "missing_deps": [],
        },
        "review": {
            "review_id": "rev-005",
            "original_event_ref": "evt-005-a",
        },
        "external_effect": False,
        "unknowns": [],
    }
    if source_version is not None:
        raw["source_version"] = source_version
    return raw


def _expect_reject(name, raw, reason_code, must_contain=None):
    """Helper: facade.execute(snapshot, raw) MUST raise UnsupportedVersionError
    with the given reason_code (still fail-closed)."""
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    try:
        facade.execute(_snapshot_v1_5(), raw)
        check(name, False, "execute() did NOT raise")
    except UnsupportedVersionError as exc:
        ok = exc.reason_code == reason_code
        detail = f"reason_code={exc.reason_code!r}"
        if must_contain:
            for token in must_contain:
                if token not in exc.message:
                    ok = False
                    detail += f" | missing {token!r} in message"
        check(name, ok, detail)
    except Exception as exc:  # bare AttributeError/TypeError etc.
        check(name, False, f"raised non-structured {type(exc).__name__}: {exc}")


# ==========================================================================
# P1 (F-04) — three-way version binding: raw must not override resolved.
# ==========================================================================
# P1 (conflict): Snapshot=1.5.0 / Adapter=1.5.0 / raw self-declares 9.9.9.
#   MUST be rejected with reason_code string == "ENGINE_VERSION_MISMATCH".
facade = EngineFacade()
facade.register_adapter("1.5.0", EngineV15Adapter())
try:
    facade.execute(_snapshot_v1_5(), _base_raw(source_version="9.9.9"))
    check("P1-raw-source-version-conflict-rejected",
          False, "execute() did NOT raise on conflicting raw source_version")
except UnsupportedVersionError as exc:
    # Assert against the LITERAL string value, not just the constant, so a
    # silent constant rename cannot mask a regression.
    ok = (
        exc.reason_code == REASON_VERSION_UNSUPPORTED
        and exc.reason_code == "ENGINE_VERSION_MISMATCH"
        and "9.9.9" in exc.message
        and "1.5.0" in exc.message
    )
    check("P1-raw-source-version-conflict-rejected",
          ok,
          f"reason_code={exc.reason_code!r} (literal ENGINE_VERSION_MISMATCH) "
          f"msg_contains_both={'9.9.9' in exc.message and '1.5.0' in exc.message}")
except Exception as exc:
    check("P1-raw-source-version-conflict-rejected",
          False, f"raised non-structured {type(exc).__name__}: {exc}")

# P1 (declaration-less): raw omits source_version -> trusted projection 1.5.0,
# with NO conflicting identity across result / projected / attestation.
facade = EngineFacade()
facade.register_adapter("1.5.0", EngineV15Adapter())
try:
    result = facade.execute(_snapshot_v1_5(), _base_raw(source_version=None))
    identity_ok = (
        result.source_version == "1.5.0"
        and result.projected_envelope.source_version == "1.5.0"
        and facade.attestations[0].source_version == "1.5.0"
    )
    check("P1-missing-raw-source-version-trusted",
          identity_ok,
          f"result={result.source_version!r} projected="
          f"{result.projected_envelope.source_version!r} attestation="
          f"{facade.attestations[0].source_version!r}")
except Exception as exc:
    check("P1-missing-raw-source-version-trusted",
          False, f"raised {type(exc).__name__}: {exc}")

# P1 (matching): raw self-declares 1.5.0 (== resolved) -> normal return, trusted.
facade = EngineFacade()
facade.register_adapter("1.5.0", EngineV15Adapter())
try:
    result = facade.execute(_snapshot_v1_5(), _base_raw(source_version="1.5.0"))
    identity_ok = (
        result.source_version == "1.5.0"
        and result.projected_envelope.source_version == "1.5.0"
        and facade.attestations[0].source_version == "1.5.0"
    )
    check("P1-matching-raw-source-version-ok",
          identity_ok,
          f"result={result.source_version!r} projected="
          f"{result.projected_envelope.source_version!r} attestation="
          f"{facade.attestations[0].source_version!r}")
except Exception as exc:
    check("P1-matching-raw-source-version-ok",
          False, f"raised {type(exc).__name__}: {exc}")

# P1 (adapter-level defense-in-depth): even if the facade Gate were somehow
# bypassed, the adapter MUST project the *trusted* ctx version, never the raw
# self-declared value. This independently exercises the adapter fix and is the
# test that catches mutation m2 (reverting the projection to raw.get(...)).
try:
    ctx = AdaptationContext(
        observation_id="obs-a2-isoadapt",
        source_version="1.5.0",
        scenario_ref="s-a2",
        enabled_capabilities=[],
    )
    raw_conflict = _base_raw(source_version="9.9.9")
    adapted = EngineV15Adapter().adapt(raw_conflict, ctx)
    ok = (
        adapted.source_version == "1.5.0"
        and adapted.projected_envelope.source_version == "1.5.0"
        and adapted.projected_envelope.source_version != "9.9.9"
    )
    check("P1-adapter-projects-trusted-version",
          ok,
          f"result={adapted.source_version!r} projected="
          f"{adapted.projected_envelope.source_version!r}")
except Exception as exc:
    check("P1-adapter-projects-trusted-version",
          False, f"raised {type(exc).__name__}: {exc}")


# ==========================================================================
# P2 (F-08) — reference integrity: mandatory non-empty reference fields.
# ==========================================================================
# P2-A: evaluation_events entry with event_digest='' MUST be rejected.
raw_a = _base_raw()
raw_a["evaluation_events"][0]["event_digest"] = ""
_expect_reject(
    "P2-empty-event-digest-rejected",
    raw_a,
    REASON_REFERENCE_UNRESOLVABLE,
)

# P2-B: review with review_id='' MUST be rejected.
raw_b = _base_raw()
raw_b["review"]["review_id"] = ""
_expect_reject(
    "P2-empty-review-id-rejected",
    raw_b,
    REASON_REFERENCE_UNRESOLVABLE,
)


# ==========================================================================
# Summary
# ==========================================================================
failed = [r for r in RESULTS if not r[1]]
print("\n" + "=" * 60)
print(f"Alpha2 independent reproduction: {len(RESULTS) - len(failed)}/{len(RESULTS)} PASS")
if failed:
    print("FAILURES:")
    for name, _, detail in failed:
        print(f"  - {name}: {detail}")
    sys.exit(1)
print("ALL ALPHA2 INDEPENDENT REPRODUCTIONS PASS")
sys.exit(0)
