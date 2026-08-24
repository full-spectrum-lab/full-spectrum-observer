#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import pathlib
import sys

import jsonschema


ROOT = pathlib.Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "schemas/scenario-pack/v1/scenario-pack-manifest.schema.json"
TRUST_STORE = ROOT / "config/scenario-pack-trust-roots.json"
PACKS = (
    ROOT / "packs/scenario-packs/knowledge-conflict/1.0.0",
    ROOT / "packs/scenario-packs/synthetic-policy-conflict/1.0.0",
)
PILOT_TEMPLATES = (
    ROOT / "docs/v0.4/pilot/AUTHORIZED_SAMPLE_MANIFEST.template.json",
    ROOT / "docs/v0.4/pilot/REDACTION_REPORT.template.md",
    ROOT / "docs/v0.4/pilot/PILOT_ACCEPTANCE_RECORD.template.md",
    ROOT / "docs/v0.4/pilot/DELETION_EXIT_RECORD.template.md",
)
V04_METRICS = {
    "discovery_rate",
    "false_positive_rate",
    "human_review_duration_milliseconds",
    "evidence_completeness_rate",
    "repeat_result_consistency_rate",
}


def encode(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")


def compute_digest(pack: pathlib.Path, manifest: dict[str, object]) -> str:
    unsigned = dict(manifest)
    unsigned.pop("digest")
    payloads: list[dict[str, str]] = []
    for path in sorted(
        (
            item
            for item in pack.rglob("*")
            if item.is_file()
            and item not in {pack / "scenario-pack.manifest.json", pack / "scenario-pack.signature.json"}
        ),
        key=lambda item: item.relative_to(pack).as_posix(),
    ):
        if path.is_symlink():
            raise ValueError(f"symlink is forbidden: {path.relative_to(pack).as_posix()}")
        payloads.append(
            {
                "path": path.relative_to(pack).as_posix(),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
        )
    material = {
        "digest_contract": "fs-observer/scenario-pack-digest/1",
        "manifest_without_digest": unsigned,
        "payload_files": payloads,
    }
    return hashlib.sha256(encode(material)).hexdigest()


def decode_base64url(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * ((4 - len(value) % 4) % 4))


def verify_signature(pack: pathlib.Path, manifest: dict[str, object], keys: dict[str, dict[str, str]]) -> None:
    path = pack / "scenario-pack.signature.json"
    signature = json.loads(path.read_text(encoding="utf-8"))
    required = {"contract", "key_id", "algorithm", "pack_id", "version", "digest", "signature_base64"}
    if set(signature) != required:
        raise ValueError(f"signature fields are not exact: {path}")
    expected_identity = {
        "contract": "fs-observer/scenario-pack-signature/1",
        "algorithm": "RS256",
        "pack_id": manifest["pack_id"],
        "version": manifest["version"],
        "digest": manifest["digest"],
    }
    if any(signature[name] != value for name, value in expected_identity.items()):
        raise ValueError(f"signature identity mismatch: {path}")
    key = keys.get(str(signature["key_id"]))
    if key is None or key["algorithm"] != "RS256":
        raise ValueError(f"untrusted signature key: {signature['key_id']}")

    material = {name: signature[name] for name in ("contract", "key_id", "algorithm", "pack_id", "version", "digest")}
    digest = hashlib.sha256(encode(material)).digest()
    digest_info = bytes.fromhex("3031300d060960864801650304020105000420") + digest
    modulus = int.from_bytes(decode_base64url(key["modulus_base64url"]), "big")
    exponent = int.from_bytes(decode_base64url(key["exponent_base64url"]), "big")
    encoded_length = (modulus.bit_length() + 7) // 8
    supplied = base64.b64decode(signature["signature_base64"], validate=True)
    encoded = pow(int.from_bytes(supplied, "big"), exponent, modulus).to_bytes(encoded_length, "big")
    padding_length = encoded_length - len(digest_info) - 3
    expected = b"\x00\x01" + b"\xff" * padding_length + b"\x00" + digest_info
    if padding_length < 8 or not hmac.compare_digest(encoded, expected):
        raise ValueError(f"signature verification failed: {path}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    args = parser.parse_args()
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    trust_store = json.loads(TRUST_STORE.read_text(encoding="utf-8"))
    if set(trust_store) != {"contract", "keys"} or trust_store["contract"] != "fs-observer/scenario-pack-trust-store/1":
        raise ValueError("invalid Scenario Pack trust store")
    keys = {str(key["key_id"]): key for key in trust_store["keys"]}
    required_key_fields = {"key_id", "algorithm", "modulus_base64url", "exponent_base64url", "purpose"}
    if len(keys) != len(trust_store["keys"]) or any(set(key) != required_key_fields for key in trust_store["keys"]):
        raise ValueError("Scenario Pack trust-store keys are duplicate or contain missing/unknown fields")
    checks: list[dict[str, str]] = []
    pack_ids: set[str] = set()

    for pack in PACKS:
        manifest_path = pack / "scenario-pack.manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        jsonschema.Draft202012Validator(schema).validate(manifest)
        actual = compute_digest(pack, manifest)
        if actual != manifest["digest"]:
            raise ValueError(f"digest mismatch for {pack}: declared={manifest['digest']} actual={actual}")
        verify_signature(pack, manifest, keys)
        if "v0.4.0-beta" not in manifest["compatible_observer_versions"]:
            raise ValueError(f"Observer compatibility missing for {pack}")
        if "v1.5.0" not in manifest["compatible_engine_versions"]:
            raise ValueError(f"Engine compatibility missing for {pack}")
        pack_id = str(manifest["pack_id"])
        if pack_id in pack_ids:
            raise ValueError(f"duplicate Pack id: {pack_id}")
        pack_ids.add(pack_id)
        checks.append({"pack_id": pack_id, "digest": actual, "status": "PASS"})

        metric_ids = {str(item["metric_id"]) for item in manifest["value_metrics"]}
        if not metric_ids.issubset(V04_METRICS):
            raise ValueError(f"unsupported v0.4 metric id in {pack}: {sorted(metric_ids - V04_METRICS)}")
        if pack.name == "1.0.0" and pack.parent.name == "knowledge-conflict":
            if metric_ids != V04_METRICS:
                raise ValueError(f"primary Pack must declare all five metrics: {sorted(metric_ids)}")
            case_ids = {str(item["case_id"]) for item in manifest["golden_cases"]}
            required_cases = {"KC_GOLDEN_001", "KC_BOUNDARY_001", "KC_ADVERSARIAL_001"}
            if not required_cases.issubset(case_ids):
                raise ValueError(f"primary Pack sample tiers are incomplete: {sorted(case_ids)}")

    for template in PILOT_TEMPLATES:
        if not template.is_file():
            raise ValueError(f"pilot template missing: {template}")
        if "DRAFT_NOT_" not in template.read_text(encoding="utf-8"):
            raise ValueError(f"pilot template must remain explicitly non-evidentiary: {template}")

    source = "\n".join(
        path.read_text(encoding="utf-8", errors="replace")
        for path in (ROOT / "src").rglob("*.cs")
    )
    forbidden = [pack_id for pack_id in pack_ids if pack_id in source]
    if forbidden:
        raise ValueError(f"scenario-specific Pack ids leaked into Core source: {forbidden}")

    report = {
        "gate": "OBSERVER_V04_SCENARIO_PACK_GATE_1",
        "status": "PASS",
        "packs": checks,
        "same_core_second_pack": "PASS",
        "detached_signatures": "2/2 PASS",
        "sample_tiers": "GOLDEN_BOUNDARY_ADVERSARIAL",
        "five_metric_declarations": "PASS",
        "pilot_template_safety": "DRAFT_NOT_EVIDENCE",
        "core_pack_id_hits": forbidden,
        "limitations": [
            "Repository gates use synthetic fixtures and establish implementation behavior only.",
            "A target-user pilot, real authorization, completed redaction report, acceptance sign-off, and deletion/exit evidence remain external requirements.",
            "No result may be generalized beyond its recorded sample and frozen version boundary.",
        ],
    }
    rendered = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        output = pathlib.Path(args.output).resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
