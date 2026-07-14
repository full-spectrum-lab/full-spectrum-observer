"""INDEPENDENT QA reproduction of the third-party defect (D1-D7) inputs.

This script does NOT import the engineer's test module. It reproduces the
exact negative inputs from the independent QA report verbatim and asserts the
fail-closed behaviours hold. It is run directly with the managed venv Python,
separate from pytest, to prove the fixes are real and not "fake green".

Exit code 0 => all independent reproductions PASS. Non-zero => at least one FAIL.
"""

from __future__ import annotations

import math
import os
import sys

# Make the standalone src/compat package importable.
_REPO = os.path.abspath(os.path.dirname(__file__))
_SRC = os.path.join(_REPO, "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from compat.adapter_interface import AdaptationContext  # noqa: E402
from compat.adapter_result import EngineEnvelope, ObserverEnvelope  # noqa: E402
from compat.canonical import canonical_json  # noqa: E402
from compat.engine_facade import EngineFacade  # noqa: E402
from compat.engine_v1_adapter import EngineV1Adapter  # noqa: E402
from compat.engine_v15_adapter import EngineV15Adapter  # noqa: E402
from compat.runtime_snapshot import RuntimeConfigurationSnapshot  # noqa: E402
from compat.schema_validator import SchemaValidator  # noqa: E402
from compat.version_resolver import (  # noqa: E402
    REASON_ADAPTER_VERSION_BINDING,
    REASON_INPUT_TYPE_INVALID,
    REASON_SNAPSHOT_PIN_MISMATCH,
    EngineVersionResolver,
    UnsupportedVersionError,
)

RESULTS = []


def check(name: str, ok: bool, detail: str = "") -> None:
    RESULTS.append((name, ok, detail))
    status = "PASS" if ok else "FAIL"
    print(f"[{status}] {name}  {detail}")


# ==========================================================================
# D3 — Snapshot must strictly pin the trusted Engine release (no forgery).
# ==========================================================================
resolver = EngineVersionResolver.default()

# Forged triple: tag v9.9.9 / commit garbage / digest not-a-sha256.
# NOTE (spec nuance): the report constructs this with an *untrusted declared
# version* ("9.9.9"). The resolver's support-matrix gate fires first and
# returns ENGINE_VERSION_MISMATCH (still a structured, fail-closed rejection —
# no silent pass). The realistic forgery (valid declared version + forged
# tag/commit/digest, see D3-realistic-forged) returns ENGINE_PIN_MISMATCH.
# The security property "forged Engine is never accepted" holds in BOTH cases.
forged = RuntimeConfigurationSnapshot(
    engine_version_declared="9.9.9",
    engine_tag="v9.9.9",
    engine_commit="garbage",
    engine_digest="not-a-sha256",
)
try:
    resolver.resolve(forged)
    check("D3-forged-triple", False, "resolve() did NOT raise on forged Engine")
except UnsupportedVersionError as exc:
    # Security property: fail-closed structured rejection. Reason code may be
    # ENGINE_VERSION_MISMATCH (untrusted declared version) or
    # ENGINE_PIN_MISMATCH.
    ok = exc.reason_code in (REASON_SNAPSHOT_PIN_MISMATCH, "ENGINE_VERSION_MISMATCH")
    check("D3-forged-triple",
          ok,
          f"fail-closed structured rejection (reason_code={exc.reason_code})")
except Exception as exc:  # bare AttributeError/TypeError etc.
    check("D3-forged-triple", False, f"raised non-structured {type(exc).__name__}: {exc}")

# Forged: real tag but garbage commit.
forged_commit = RuntimeConfigurationSnapshot(
    engine_version_declared="1.5.0",
    engine_tag="v1.5.0",
    engine_commit="garbage",
    engine_digest="sha256:f1836bb56245c1f5cd7f6496aef504e1bdd3bb16b2255ee5af94ced215ac73cb",
)
try:
    resolver.resolve(forged_commit)
    check("D3-forged-commit", False, "resolve() did NOT raise on forged commit")
except UnsupportedVersionError as exc:
    check("D3-forged-commit", exc.reason_code == REASON_SNAPSHOT_PIN_MISMATCH,
          f"reason_code={exc.reason_code}")
except Exception as exc:
    check("D3-forged-commit", False, f"raised non-structured {type(exc).__name__}: {exc}")

# Forged: real tag + commit but garbage digest.
forged_digest = RuntimeConfigurationSnapshot(
    engine_version_declared="1.5.0",
    engine_tag="v1.5.0",
    engine_commit="f6eb92aee24a706f1b71dc073de6a760fca31092",
    engine_digest="not-a-sha256",
)
try:
    resolver.resolve(forged_digest)
    check("D3-forged-digest", False, "resolve() did NOT raise on forged digest")
except UnsupportedVersionError as exc:
    check("D3-forged-digest", exc.reason_code == REASON_SNAPSHOT_PIN_MISMATCH,
          f"reason_code={exc.reason_code}")
except Exception as exc:
    check("D3-forged-digest", False, f"raised non-structured {type(exc).__name__}: {exc}")

# Realistic forgery per report intent: declare a VALID supported version
# (1.5.0) but forge the tag/commit/digest -> must be ENGINE_PIN_MISMATCH.
realistic_forged = RuntimeConfigurationSnapshot(
    engine_version_declared="1.5.0",
    engine_tag="v9.9.9",
    engine_commit="garbage",
    engine_digest="not-a-sha256",
)
try:
    resolver.resolve(realistic_forged)
    check("D3-realistic-forged", False, "resolve() did NOT raise on forged tag")
except UnsupportedVersionError as exc:
    check("D3-realistic-forged", exc.reason_code == REASON_SNAPSHOT_PIN_MISMATCH,
          f"reason_code={exc.reason_code}")
except Exception as exc:
    check("D3-realistic-forged", False, f"raised non-structured {type(exc).__name__}: {exc}")

# POSITIVE: real v1.0.0 anchor must pass.
try:
    resolved = resolver.resolve(RuntimeConfigurationSnapshot.frozen_v1_0_0())
    check("D3-real-v1.0.0", resolved.version == "1.0.0",
          f"resolved.version={getattr(resolved, 'version', None)}")
except Exception as exc:
    check("D3-real-v1.0.0", False, f"raised {type(exc).__name__}: {exc}")

# POSITIVE: real v1.5.0 anchor must pass.
try:
    resolved = resolver.resolve(RuntimeConfigurationSnapshot.frozen_v1_5_0())
    check("D3-real-v1.5.0", resolved.version == "1.5.0",
          f"resolved.version={getattr(resolved, 'version', None)}")
except Exception as exc:
    check("D3-real-v1.5.0", False, f"raised {type(exc).__name__}: {exc}")


# ==========================================================================
# D4 — adapter version binding is strict (no V1 adapter under 1.5, etc.).
# ==========================================================================
facade = EngineFacade()
try:
    facade.register_adapter("1.5.0", EngineV1Adapter())
    check("D4-v1-adapter-under-1.5", False, "register did NOT raise binding error")
except UnsupportedVersionError as exc:
    check("D4-v1-adapter-under-1.5",
          exc.reason_code == REASON_ADAPTER_VERSION_BINDING,
          f"reason_code={exc.reason_code}")
except Exception as exc:
    check("D4-v1-adapter-under-1.5", False, f"raised {type(exc).__name__}: {exc}")

facade2 = EngineFacade()
try:
    facade2.register_adapter("1.0.0", EngineV15Adapter())
    check("D4-v15-adapter-under-1.0", False, "register did NOT raise binding error")
except UnsupportedVersionError as exc:
    check("D4-v15-adapter-under-1.0",
          exc.reason_code == REASON_ADAPTER_VERSION_BINDING,
          f"reason_code={exc.reason_code}")
except Exception as exc:
    check("D4-v15-adapter-under-1.0", False, f"raised {type(exc).__name__}: {exc}")


# ==========================================================================
# D5 — strict dual schema validation (no silent coercion).
# ==========================================================================
validator = SchemaValidator()

# envelope_version="evil-envelope" -> reject.
env_evil = EngineEnvelope.build(envelope_version="evil-envelope", payload={})
chk = validator.validate_engine(env_evil)
check("D5-evil-envelope", (not chk.ok) and "ENGINE_ENVELOPE_VERSION_UNSUPPORTED" in chk.field_errors,
      f"field_errors={chk.field_errors}")

# evaluation_events="not-a-list" (string) -> reject.
obs_ev = ObserverEnvelope(source_version="1.5.0", evaluation_events="not-a-list")
chk = validator.validate_observer(obs_ev)
check("D5-evaluation-events-string",
      (not chk.ok) and "OBS_EVALUATION_EVENTS_TYPE_INVALID" in chk.field_errors,
      f"field_errors={chk.field_errors}")

# unknowns="abc" (string) -> reject, NEVER silently split into ["a","b","c"].
obs_unk = ObserverEnvelope(source_version="1.5.0", unknowns="abc")
chk = validator.validate_observer(obs_unk)
check("D5-unknowns-string",
      (not chk.ok) and "OBS_UNKNOWNS_TYPE_INVALID" in chk.field_errors,
      f"field_errors={chk.field_errors}")

# Adapter must NOT silently turn unknowns="abc" into ["a","b","c"].
ctx = AdaptationContext(observation_id="obs", source_version="1.5.0")
result = EngineV15Adapter().adapt({"unknowns": "abc"}, ctx)
check("D5-no-silent-split",
      result.projected_envelope.unknowns == "abc"
      and result.projected_envelope.unknowns != ["a", "b", "c"],
      f"unknowns={result.projected_envelope.unknowns!r}")

# POSITIVE: valid inputs must pass the normal path.
good_engine = EngineEnvelope.build(envelope_version="1.2", payload={})
chk = validator.validate_engine(good_engine)
check("D5-valid-engine", chk.ok, f"field_errors={chk.field_errors}")
# NB: ObserverEnvelope must be finalized so canonical_digest is populated
# (the validator correctly requires a non-empty digest — this is a source
# contract, not a bug). .finalize() computes the deterministic digest.
good_obs = ObserverEnvelope(
    source_version="1.5.0",
    evaluation_events=["e1"],
    unknowns=["subject_declaration"],
    external_effect=False,
).finalize()
chk = validator.validate_observer(good_obs)
check("D5-valid-observer", chk.ok, f"field_errors={chk.field_errors}")


# ==========================================================================
# D6 — non-expected input types return structured errors, not raw exceptions.
# ==========================================================================
def expect_structured(label, fn, expected_reason):
    try:
        fn()
        check(label, False, "did NOT raise")
    except UnsupportedVersionError as exc:
        check(label, exc.reason_code == expected_reason,
              f"reason_code={exc.reason_code}")
    except (AttributeError, TypeError, ValueError) as exc:
        check(label, False, f"raised bare {type(exc).__name__}: {exc}")
    except Exception as exc:
        check(label, False, f"raised unexpected {type(exc).__name__}: {exc}")


snap = RuntimeConfigurationSnapshot.frozen_v1_5_0()
expect_structured("D6-resolve-none", lambda: resolver.resolve(None), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-resolve-string", lambda: resolver.resolve("garbage"), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-resolve-array", lambda: resolver.resolve([1, 2, 3]), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-resolve-int", lambda: resolver.resolve(123), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-execute-none-snapshot",
                  lambda: EngineFacade().execute(None, {}), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-execute-non-dict",
                  lambda: EngineFacade().execute(snap, "not-a-dict"), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-execute-array",
                  lambda: EngineFacade().execute(snap, [1, 2, 3]), REASON_INPUT_TYPE_INVALID)
expect_structured("D6-execute-int",
                  lambda: EngineFacade().execute(snap, 123), REASON_INPUT_TYPE_INVALID)


# ==========================================================================
# D7 — canonical JSON rejects non-finite floats (NaN / Infinity).
# ==========================================================================
try:
    canonical_json({"x": float("nan")})
    check("D7-nan", False, "canonical_json did NOT raise on NaN")
except ValueError:
    check("D7-nan", True, "raised ValueError as expected")
except Exception as exc:
    check("D7-nan", False, f"raised {type(exc).__name__} instead of ValueError: {exc}")

try:
    canonical_json({"x": float("inf")})
    check("D7-inf", False, "canonical_json did NOT raise on +Inf")
except ValueError:
    check("D7-inf", True, "raised ValueError as expected")
except Exception as exc:
    check("D7-inf", False, f"raised {type(exc).__name__} instead of ValueError: {exc}")

try:
    canonical_json({"x": float("-inf")})
    check("D7-neginf", False, "canonical_json did NOT raise on -Inf")
except ValueError:
    check("D7-neginf", True, "raised ValueError as expected")
except Exception as exc:
    check("D7-neginf", False, f"raised {type(exc).__name__} instead of ValueError: {exc}")

# POSITIVE: valid finite float is accepted.
try:
    out = canonical_json({"x": 1.5})
    check("D7-valid-float", out == '{"x":1.5}', f"output={out!r}")
except Exception as exc:
    check("D7-valid-float", False, f"raised {type(exc).__name__}: {exc}")


# ==========================================================================
# Summary
# ==========================================================================
failed = [r for r in RESULTS if not r[1]]
print("\n" + "=" * 60)
print(f"Independent reproduction: {len(RESULTS) - len(failed)}/{len(RESULTS)} PASS")
if failed:
    print("FAILURES:")
    for name, _, detail in failed:
        print(f"  - {name}: {detail}")
    sys.exit(1)
print("ALL INDEPENDENT REPRODUCTIONS PASS")
sys.exit(0)
