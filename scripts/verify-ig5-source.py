#!/usr/bin/env python3
from __future__ import annotations

import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXECUTION = ROOT / "src" / "Observer.Execution"
HOST = ROOT / "src" / "Observer.Host.Cli"
EVIDENCE = ROOT / "evidence" / "ig5" / "source-static.json"

checks: list[dict[str, str]] = []


def check(name: str, condition: bool, detail: str) -> None:
    checks.append(
        {
            "check": name,
            "status": "PASS" if condition else "FAIL",
            "detail": detail,
        }
    )


required_execution = [
    "FoundationAnalysisUseCase.cs",
    "FoundationInputIntake.cs",
    "FoundationScenarioAdapter.cs",
    "FoundationValidationPipeline.cs",
    "RuntimeConfigurationResolver.cs",
    "GovernanceOutputAssembler.cs",
    "FoundationReadUseCases.cs",
    "ExecutionComposition.cs",
]
for name in required_execution:
    check(f"FILE-{name}", (EXECUTION / name).is_file(), name)

execution_text = "\n".join(
    path.read_text(encoding="utf-8", errors="ignore")
    for path in EXECUTION.glob("*.cs")
)
host_text = "\n".join(
    path.read_text(encoding="utf-8", errors="ignore")
    for path in HOST.glob("*.cs")
)
system_text = execution_text + "\n" + host_text

check(
    "VAC-FK-001",
    "run_simulation" not in system_text
    and "CalculateFshi" not in system_text
    and "CalculateRisk" not in system_text,
    "No formal Engine algorithm implementation or direct Engine function call in System code.",
)
check(
    "NO-HTTP",
    "HttpClient" not in system_text
    and "WebApplication" not in system_text
    and "Kestrel" not in system_text,
    "No HTTP entry point in v0.1 source candidate.",
)
check(
    "NO-DEFERRED-PRODUCTS",
    all(token not in system_text for token in ["Copilot", "Connector", "Registry", "Observer.Console"]),
    "No Console/Copilot/Connector/Registry implementation.",
)
check(
    "BOUNDARY-CONSTANTS",
    all(
        token in execution_text
        for token in [
            "observer_only = true",
            "certified = false",
            "authorized = false",
            "active_external = false",
        ]
    ),
    "Observer-only boundary constants are explicit.",
)
check(
    "CLI-COMMANDS",
    all(token in host_text for token in ['"health"', '"analyze"', '"show"', '"verify-audit"']),
    "Required CLI commands are present.",
)
check(
    "EXECUTION-USES-PORTS",
    "FullSpectrum.Observer.Evidence" not in execution_text
    and "FullSpectrum.Observer.EngineFacade" not in execution_text,
    "Execution project does not reference concrete Evidence or EngineFacade namespaces.",
)
analysis_source = (EXECUTION / "FoundationAnalysisUseCase.cs").read_text(encoding="utf-8")
check(
    "RAW-INPUT-NOT-FINALIZED",
    "intake.RawBytes" not in analysis_source
    and "RawBytes" not in re.sub(r"record IntakeResult.*?;", "", analysis_source, flags=re.S),
    "Finalization receives input/context digests rather than raw input.",
)
check(
    "ENGINE-FACADE-SINGLE-PORT",
    analysis_source.count("_engine.EvaluateAsync") == 1,
    "Execution pipeline has one Engine Facade invocation.",
)
check(
    "CANCELLATION-STATE-PATH",
    "OperationStates.Cancelling" in analysis_source
    and "OperationStates.Cancelled" in analysis_source,
    "Cancellation uses CANCELLING then CANCELLED state path.",
)
check(
    "SCHEMA-GOVERNANCE-SEPARATE",
    "ValidateRequest" in analysis_source
    and "ValidateGovernance" in analysis_source,
    "Schema and Governance validation are separate stages.",
)

# Conservative lexical balance check, not a compiler.
def balanced(text: str) -> bool:
    stripped = re.sub(r'//.*?$|/\*.*?\*/|@?"(?:""|\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', '', text, flags=re.M | re.S)
    pairs = {')': '(', ']': '[', '}': '{'}
    stack: list[str] = []
    for character in stripped:
        if character in "([{":
            stack.append(character)
        elif character in ")]}":
            if not stack or stack.pop() != pairs[character]:
                return False
    return not stack

cs_files = list(EXECUTION.glob("*.cs")) + list(HOST.glob("*.cs"))
check(
    "LEXICAL-BALANCE",
    all(balanced(path.read_text(encoding="utf-8")) for path in cs_files),
    f"Balanced braces/parentheses for {len(cs_files)} WP-04/WP-05 C# files.",
)

status = "PASS" if all(item["status"] == "PASS" for item in checks) else "FAIL"
report = {
    "report_id": "IG5_SOURCE_STATIC",
    "status": status,
    "formal_gate": "NOT_PASSED",
    "checks": checks,
    "note": "Static source verification is not a .NET compilation or Windows integration result.",
}
EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
EVIDENCE.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
print(json.dumps(report, ensure_ascii=False, indent=2))
raise SystemExit(0 if status == "PASS" else 1)
