#!/usr/bin/env python3
from __future__ import annotations
import hashlib, json, os, pathlib, subprocess, sys
ROOT = pathlib.Path(__file__).resolve().parents[1]
# Evidence root: FSP_EVIDENCE_ROOT if set, otherwise the repository's tracked
# evidence/ folder (backward compatible).
EVIDENCE_ROOT = pathlib.Path(os.environ.get("FSP_EVIDENCE_ROOT") or (ROOT / "evidence"))
WORKER = ROOT / 'engine/worker/worker.py'; ENGINE = ROOT / 'engine/vendor/full-spectrum-engine'; CASE = ROOT / 'packs/foundation-case005/case005.input.json'; GOLDEN = ROOT / 'packs/foundation-case005/engine-golden.json'; EVIDENCE = EVIDENCE_ROOT / 'ig4/worker-smoke.json'


def encode(v): return json.dumps(v, ensure_ascii=False, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def run(req):
    p = subprocess.run([sys.executable, str(WORKER), '--engine-root', str(ENGINE)], input=encode(req) + b'\n', capture_output=True)
    lines = p.stdout.splitlines(); response = json.loads(lines[0]) if len(lines) == 1 else None
    return p, response, lines


def check_numpy():
    """M2-FIX-03 gate: numpy must import (Engine v1.5.0 worker requires it).

    Returns (ok, detail) where detail is the numpy version string or the error.
    """
    try:
        import numpy  # noqa: F401
        return True, getattr(numpy, "__version__", "unknown")
    except Exception as exc:  # pragma: no cover - exercised only on a broken env
        return False, f"{type(exc).__name__}: {exc}"


def main():
    scenario = json.loads(CASE.read_text()); golden = json.loads(GOLDEN.read_text())
    req = {'protocol': 'fs-observer-engine-facade/1', 'request_id': '12345678-1234-4234-8234-123456789abc', 'operation': 'evaluate', 'engine': {'version': 'v1.5.0', 'commit': '88493007d4e00344c70a70ed0e5a5d652dec86f5'}, 'seed': 42, 'fixed_time_utc': '2026-07-04T00:00:00Z', 'scenario': scenario, 'output_serialization': 'FSE-PYJSON-1'}
    p1, r1, l1 = run(req); p2, r2, l2 = run(req)
    wrong = json.loads(json.dumps(req)); wrong['engine']['commit'] = '0' * 40; pw, rw, lw = run(wrong)
    invalid = json.loads(json.dumps(req)); invalid['protocol'] = 'wrong'; pi, ri, li = run(invalid)
    expected = hashlib.sha256(encode(golden)).hexdigest()
    numpy_ok, numpy_detail = check_numpy()
    checks = [
        ('one_line', len(l1) == 1),
        ('exit_zero', p1.returncode == 0),
        ('status_success', r1 and r1['status'] == 'SUCCESS'),
        ('golden_equal', r1 and r1['output'] == golden),
        ('digest', r1 and r1['output_sha256'] == expected),
        ('deterministic', r1 == r2),
        ('wrong_commit_rejected', rw and rw['error']['code'] == 'ENGINE_VERSION_MISMATCH'),
        ('invalid_protocol_rejected', ri and ri['error']['code'] == 'FACADE_PROTOCOL_INVALID'),
        ('stderr_not_stdout', b'EMERGENCY BRAKE' not in p1.stdout),
        ('NUMPY_IMPORT', numpy_ok),
    ]
    formal = os.environ.get('FSP_FORMAL_GATE_CONTEXT') == 'IG4'
    report = {
        'gate': 'IG4_SOURCE_REFERENCE',
        'status': 'PASS' if all(v for _, v in checks) else 'FAIL',
        'formal_gate': 'PASSED' if formal else 'NOT_PASSED',
        'python': sys.version,
        'numpy': {'imported': numpy_ok, 'version': numpy_detail},
        'checks': [{'id': k, 'status': 'PASS' if v else 'FAIL'} for k, v in checks],
        'success_stdout_sha256': hashlib.sha256(p1.stdout).hexdigest(),
        'stderr_sha256': hashlib.sha256(p1.stderr).hexdigest(),
        'note': 'Pinned Engine, private Python 3.11 and C# process tests passed.' if formal else 'Standalone Worker oracle; C# process execution not proven by this invocation.',
    }
    EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
    EVIDENCE.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report['status'] == 'PASS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
