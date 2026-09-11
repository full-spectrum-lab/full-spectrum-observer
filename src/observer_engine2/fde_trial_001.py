"""FDE-TRIAL-001 frozen fixture validation and lossless projection."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime
import hashlib
import json
from pathlib import Path
import subprocess
from typing import Any, Mapping

import jsonschema
import rfc8785

from .messages import SnapshotBinding


FDE_CASE_ID = "FDE-TRIAL-001"
FDE_FIXTURE_VERSION = "0.1.0-rc"
PROTOCOL_CONTRACT_BASELINE_COMMIT = "e08822cce605f14958c30f736dd0b0be3719de32"
FIXTURE_RELATIVE_PATH = Path("fixtures/fde-trial-001/0.1.0-rc")
SCHEMA_RELATIVE_PATH = Path("schemas/fde-trial-001-fixture.schema.json")


class FdeFixtureValidationError(ValueError):
    """A frozen FDE fixture cannot be safely projected."""

    def __init__(self, error_code: str, message: str):
        super().__init__(f"{error_code}: {message}")
        self.error_code = error_code


@dataclass(frozen=True)
class FdeFixtureProjection:
    protocol_object: Mapping[str, Any]
    protocol_object_digest: str
    snapshot_binding: SnapshotBinding

    def to_dict(self) -> dict[str, Any]:
        return {
            "observer_projection": {
                "protocol_object": deepcopy(dict(self.protocol_object)),
                "protocol_object_digest": self.protocol_object_digest,
            }
        }


class FdeTrial001FixtureAdapter:
    """Validate the Protocol-owned frozen fixture and project complete documents."""

    def __init__(self, protocol_checkout: str | Path):
        self.protocol_checkout = Path(protocol_checkout).resolve()
        try:
            head = subprocess.run(
                ["git", "-C", str(self.protocol_checkout), "rev-parse", "HEAD"],
                check=True,
                capture_output=True,
                text=True,
            ).stdout.strip()
        except (OSError, subprocess.CalledProcessError) as exc:
            raise FdeFixtureValidationError(
                "REPLAY_DEPENDENCY_MISSING", "Protocol checkout is unavailable"
            ) from exc
        if head != PROTOCOL_CONTRACT_BASELINE_COMMIT:
            raise FdeFixtureValidationError(
                "REPLAY_DEPENDENCY_MISSING",
                f"Protocol checkout must be exactly {PROTOCOL_CONTRACT_BASELINE_COMMIT}; got {head}",
            )

    def project(self) -> FdeFixtureProjection:
        try:
            return self._project()
        except FdeFixtureValidationError:
            raise
        except (KeyError, IndexError, TypeError, ValueError) as exc:
            raise FdeFixtureValidationError(
                "FDE_FIXTURE_STRUCTURE_INVALID", str(exc)
            ) from exc

    def _project(self) -> FdeFixtureProjection:
        fixture_root = self.protocol_checkout / FIXTURE_RELATIVE_PATH
        schema_path = self.protocol_checkout / SCHEMA_RELATIVE_PATH
        manifest = self._load_json(fixture_root / "CASE-MANIFEST.json")
        schema = self._load_json(schema_path)
        self._validate_manifest(manifest, schema_path)

        expected_files = manifest["files"]
        actual_files = {path.name for path in fixture_root.glob("*.json")} - {"CASE-MANIFEST.json"}
        if actual_files != set(expected_files):
            self._fail("FDE_FIXTURE_DOCUMENT_SET_INVALID", "fixture document set differs from manifest")

        documents = {
            filename: self._load_json(fixture_root / filename)
            for filename in expected_files
        }
        document_types = [document.get("document_type") for document in documents.values()]
        if len(set(document_types)) != len(document_types):
            self._fail("FDE_FIXTURE_DUPLICATE_DOCUMENT", "duplicate document_type")

        validator = jsonschema.Draft202012Validator(schema, format_checker=jsonschema.FormatChecker())
        for filename, expected_digest in expected_files.items():
            document = documents[filename]
            try:
                validator.validate(document)
            except jsonschema.ValidationError as exc:
                self._fail("FDE_FIXTURE_SCHEMA_INVALID", f"{filename}: {exc.message}")
            if self._canonical_digest(document) != expected_digest:
                self._fail("EVIDENCE_DIGEST_MISMATCH", filename)

        projection_contract = manifest["observer_projection_contract"]
        self._validate_projection_contract(projection_contract, expected_files, documents)
        self._validate_policy_source_digests(documents)
        self._validate_cross_document_bindings(manifest, documents)

        protocol_object = {
            projection_contract["document_keys"][filename]: deepcopy(document)
            for filename, document in documents.items()
        }
        snapshot = documents["snapshot-binding.json"]["payload"]
        return FdeFixtureProjection(
            protocol_object=protocol_object,
            protocol_object_digest=self._canonical_digest(protocol_object),
            snapshot_binding=SnapshotBinding(**snapshot),
        )

    def _validate_manifest(self, manifest: dict[str, Any], schema_path: Path) -> None:
        required = {
            "case_id": FDE_CASE_ID,
            "fixture_status": "FROZEN",
            "fixture_version": FDE_FIXTURE_VERSION,
            "canonicalization": "RFC8785_JSON",
            "digest_algorithm": "SHA-256_UPPERCASE_HEX",
            "digest_scope": "PARSED_COMPLETE_DOCUMENT",
            "schema": str(SCHEMA_RELATIVE_PATH).replace("\\", "/"),
        }
        if any(manifest.get(key) != value for key, value in required.items()):
            self._fail("FDE_FIXTURE_MANIFEST_INVALID", "frozen manifest metadata mismatch")
        binding = manifest.get("repository_binding", {})
        if binding.get("protocol_contract_commit") != "95ac7121d2500a790450a6c4c8ae2f5c0d1b0c8e":
            self._fail("REPLAY_DEPENDENCY_MISSING", "Protocol contract commit mismatch")
        schema_digest = hashlib.sha256(schema_path.read_bytes()).hexdigest().upper()
        if schema_digest != binding.get("schema_file_sha256"):
            self._fail("EVIDENCE_DIGEST_MISMATCH", "schema_file_sha256")

    def _validate_projection_contract(
        self,
        contract: dict[str, Any],
        expected_files: Mapping[str, str],
        documents: Mapping[str, dict[str, Any]],
    ) -> None:
        if contract.get("source_documents_are_projected_in_full") is not True:
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "full document projection is required")
        if contract.get("protocol_object_path") != "/observer_projection/protocol_object":
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "protocol_object path mismatch")
        if contract.get("protocol_object_digest_path") != "/observer_projection/protocol_object_digest":
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "protocol_object_digest path mismatch")
        document_keys = contract.get("document_keys", {})
        pointers = contract.get("protected_json_pointers", {})
        if set(document_keys) != set(expected_files) or len(set(document_keys.values())) != len(expected_files):
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "document_keys are incomplete or conflicting")
        if set(pointers) != set(expected_files):
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "protected pointer sets are incomplete")
        required_rules = {
            "object_member_order_semantics": "ORDER_INDEPENDENT_RFC8785",
            "array_order": "PRESERVE",
            "scalar_types": "PRESERVE",
            "duplicate_or_missing_document": "FAIL_CLOSED",
            "unexpected_document": "FAIL_CLOSED",
            "conflicting_value": "FAIL_CLOSED",
            "digest_mismatch": "FAIL_CLOSED",
        }
        rules = contract.get("assembly_rules", {})
        if any(rules.get(key) != value for key, value in required_rules.items()):
            self._fail("FDE_PROJECTION_CONTRACT_INVALID", "assembly rules mismatch")
        for filename, protected in pointers.items():
            for pointer in protected:
                try:
                    self._resolve_pointer(documents[filename], pointer)
                except (KeyError, IndexError, TypeError, ValueError):
                    self._fail("FDE_PROTECTED_FIELD_MISSING", f"{filename}:{pointer}")

    def _validate_policy_source_digests(self, documents: Mapping[str, dict[str, Any]]) -> None:
        for filename in ("policy-before.json", "policy-proposed.json"):
            payload = documents[filename]["payload"]
            if payload["source_digest_scope"] != "UTF8_BYTES_OF_PAYLOAD_RULE_NO_TERMINATOR":
                self._fail("FDE_POLICY_SOURCE_DIGEST_INVALID", f"{filename}: unsupported digest scope")
            digest = hashlib.sha256(payload["rule"].encode("utf-8")).hexdigest().upper()
            if digest != payload["source_content_sha256"]:
                self._fail("FDE_POLICY_SOURCE_DIGEST_INVALID", filename)

    def _validate_cross_document_bindings(
        self, manifest: Mapping[str, Any], documents: Mapping[str, dict[str, Any]]
    ) -> None:
        before = documents["policy-before.json"]["payload"]
        proposed = documents["policy-proposed.json"]["payload"]
        snapshot = documents["snapshot-binding.json"]["payload"]
        authority = documents["actor-and-authority.json"]["payload"]["human_approver"]
        expected = manifest["knowledge_snapshot_binding"]
        conflicts = (
            proposed["parent_snapshot_ref"] != before["snapshot_ref"],
            proposed["candidate_snapshot_ref"] != snapshot["knowledge_snapshot_ref"],
            expected["parent_snapshot_ref"] != before["snapshot_ref"],
            expected["candidate_snapshot_ref"] != proposed["candidate_snapshot_ref"],
            expected["candidate_policy_document_digest"] != manifest["files"]["policy-proposed.json"],
            authority["policy_id"] != proposed["policy_id"],
            authority["policy_version"] != proposed["version"],
            datetime.fromisoformat(authority["issued_at"].replace("Z", "+00:00"))
            >= datetime.fromisoformat(authority["expires_at"].replace("Z", "+00:00")),
        )
        if any(conflicts):
            self._fail("FDE_FIXTURE_CONFLICTING_VALUE", "cross-document binding mismatch")

    @staticmethod
    def _load_json(path: Path) -> dict[str, Any]:
        try:
            value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
        except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
            raise FdeFixtureValidationError("FDE_FIXTURE_JSON_INVALID", f"{path.name}: {exc}") from exc
        if not isinstance(value, dict):
            raise FdeFixtureValidationError("FDE_FIXTURE_JSON_INVALID", f"{path.name}: object required")
        return value

    @staticmethod
    def _canonical_digest(value: Any) -> str:
        try:
            return hashlib.sha256(rfc8785.dumps(value)).hexdigest().upper()
        except (TypeError, ValueError) as exc:
            raise FdeFixtureValidationError("FDE_FIXTURE_CANONICALIZATION_FAILED", str(exc)) from exc

    @staticmethod
    def _resolve_pointer(document: Any, pointer: str) -> Any:
        if not pointer.startswith("/"):
            raise ValueError("absolute JSON Pointer required")
        current = document
        for raw in pointer[1:].split("/"):
            token = raw.replace("~1", "/").replace("~0", "~")
            current = current[int(token)] if isinstance(current, list) else current[token]
        return current

    @staticmethod
    def _fail(error_code: str, message: str) -> None:
        raise FdeFixtureValidationError(error_code, message)


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result
