from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

import pytest
import rfc8785

from observer_engine2.fde_trial_001 import (
    FdeFixtureValidationError,
    FdeTrial001FixtureAdapter,
    PROTOCOL_CONTRACT_BASELINE_COMMIT,
)


PROTOCOL_CHECKOUT_VALUE = os.environ.get("FDE_PROTOCOL_CHECKOUT")
PROTOCOL_CHECKOUT = Path(PROTOCOL_CHECKOUT_VALUE) if PROTOCOL_CHECKOUT_VALUE else None
pytestmark = pytest.mark.skipif(
    PROTOCOL_CHECKOUT is None,
    reason="FDE_PROTOCOL_CHECKOUT is not configured",
)
FIXTURE = Path("fixtures/fde-trial-001/0.1.0-rc")


def digest(value):
    return hashlib.sha256(rfc8785.dumps(value)).hexdigest().upper()


def json_load(path):
    return json.loads(path.read_text(encoding="utf-8"))


def json_write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


@pytest.fixture()
def checkout(tmp_path, monkeypatch):
    assert PROTOCOL_CHECKOUT is not None
    target = tmp_path / "protocol"
    shutil.copytree(PROTOCOL_CHECKOUT / FIXTURE, target / FIXTURE)
    schema_target = target / "schemas"
    schema_target.mkdir()
    shutil.copy2(
        PROTOCOL_CHECKOUT / "schemas/fde-trial-001-fixture.schema.json",
        schema_target / "fde-trial-001-fixture.schema.json",
    )
    subprocess.run(["git", "init", "-q", str(target)], check=True)
    subprocess.run(["git", "-C", str(target), "config", "user.email", "test@example.invalid"], check=True)
    subprocess.run(["git", "-C", str(target), "config", "user.name", "test"], check=True)
    subprocess.run(["git", "-C", str(target), "add", "."], check=True)
    subprocess.run(["git", "-C", str(target), "commit", "-qm", "fixture"], check=True)
    head = subprocess.run(
        ["git", "-C", str(target), "rev-parse", "HEAD"], check=True, capture_output=True, text=True
    ).stdout.strip()
    monkeypatch.setattr("observer_engine2.fde_trial_001.PROTOCOL_CONTRACT_BASELINE_COMMIT", head)
    return target


def update_document_digest(checkout, filename):
    fixture = checkout / FIXTURE
    manifest = json_load(fixture / "CASE-MANIFEST.json")
    manifest["files"][filename] = digest(json_load(fixture / filename))
    json_write(fixture / "CASE-MANIFEST.json", manifest)


def assert_error(checkout, code):
    with pytest.raises(FdeFixtureValidationError) as exc:
        FdeTrial001FixtureAdapter(checkout).project()
    assert exc.value.error_code == code


def test_frozen_fixture_projects_complete_documents_losslessly():
    assert PROTOCOL_CHECKOUT is not None
    projection = FdeTrial001FixtureAdapter(PROTOCOL_CHECKOUT).project()
    manifest = json_load(PROTOCOL_CHECKOUT / FIXTURE / "CASE-MANIFEST.json")
    expected = {
        key: json_load(PROTOCOL_CHECKOUT / FIXTURE / filename)
        for filename, key in manifest["observer_projection_contract"]["document_keys"].items()
    }
    assert projection.protocol_object == expected
    assert projection.protocol_object_digest == digest(expected)
    assert projection.to_dict()["observer_projection"]["protocol_object"] == expected
    assert projection.snapshot_binding.to_dict() == expected["snapshot_binding"]["payload"]


def test_array_order_and_scalar_types_are_preserved():
    assert PROTOCOL_CHECKOUT is not None
    projection = FdeTrial001FixtureAdapter(PROTOCOL_CHECKOUT).project()
    proposed = projection.protocol_object["policy_proposed"]["payload"]
    order = projection.protocol_object["order_facts"]["payload"]
    assert proposed["risk_scope"] == ["HIGH_VALUE", "HIGH_RISK", "OPENED_PACKAGE"]
    assert isinstance(order["synthetic"], bool)
    assert isinstance(order["package_opened"], bool)
    assert isinstance(order["order_ref"], str)


def test_projection_is_detached_from_loaded_source():
    assert PROTOCOL_CHECKOUT is not None
    projection = FdeTrial001FixtureAdapter(PROTOCOL_CHECKOUT).project()
    value = projection.to_dict()
    value["observer_projection"]["protocol_object"]["policy_before"]["payload"]["status"] = "CHANGED"
    assert projection.protocol_object["policy_before"]["payload"]["status"] == "ACTIVE"


def test_missing_document_fails_closed(checkout):
    (checkout / FIXTURE / "order-facts.json").unlink()
    assert_error(checkout, "FDE_FIXTURE_DOCUMENT_SET_INVALID")


def test_missing_manifest_structure_fails_closed(checkout):
    path = checkout / FIXTURE / "CASE-MANIFEST.json"
    manifest = json_load(path)
    del manifest["observer_projection_contract"]
    json_write(path, manifest)
    assert_error(checkout, "FDE_FIXTURE_STRUCTURE_INVALID")


def test_unknown_document_fails_closed(checkout):
    shutil.copy2(checkout / FIXTURE / "order-facts.json", checkout / FIXTURE / "unknown.json")
    assert_error(checkout, "FDE_FIXTURE_DOCUMENT_SET_INVALID")


def test_duplicate_document_type_fails_closed(checkout):
    path = checkout / FIXTURE / "order-facts.json"
    document = json_load(path)
    document["document_type"] = "POLICY_BEFORE"
    json_write(path, document)
    update_document_digest(checkout, "order-facts.json")
    assert_error(checkout, "FDE_FIXTURE_DUPLICATE_DOCUMENT")


def test_complete_document_digest_mismatch_fails_closed(checkout):
    path = checkout / FIXTURE / "order-facts.json"
    document = json_load(path)
    document["payload"]["order_ref"] = "tampered"
    json_write(path, document)
    assert_error(checkout, "EVIDENCE_DIGEST_MISMATCH")


def test_policy_source_digest_mismatch_fails_closed(checkout):
    path = checkout / FIXTURE / "policy-before.json"
    document = json_load(path)
    document["payload"]["source_content_sha256"] = "0" * 64
    json_write(path, document)
    update_document_digest(checkout, "policy-before.json")
    assert_error(checkout, "FDE_POLICY_SOURCE_DIGEST_INVALID")


def test_conflicting_cross_document_value_fails_closed(checkout):
    path = checkout / FIXTURE / "policy-proposed.json"
    document = json_load(path)
    document["payload"]["parent_snapshot_ref"] = "synthetic://conflict"
    json_write(path, document)
    update_document_digest(checkout, "policy-proposed.json")
    manifest_path = checkout / FIXTURE / "CASE-MANIFEST.json"
    manifest = json_load(manifest_path)
    manifest["knowledge_snapshot_binding"]["candidate_policy_document_digest"] = manifest["files"]["policy-proposed.json"]
    json_write(manifest_path, manifest)
    assert_error(checkout, "FDE_FIXTURE_CONFLICTING_VALUE")


def test_duplicate_json_key_fails_closed(checkout):
    path = checkout / FIXTURE / "order-facts.json"
    path.write_text('{"case_id":"FDE-TRIAL-001","case_id":"duplicate"}', encoding="utf-8")
    assert_error(checkout, "FDE_FIXTURE_JSON_INVALID")


def test_protocol_checkout_must_match_contract_baseline(tmp_path):
    checkout = tmp_path / "repo"
    checkout.mkdir()
    subprocess.run(["git", "init", "-q", str(checkout)], check=True)
    subprocess.run(["git", "-C", str(checkout), "config", "user.email", "test@example.invalid"], check=True)
    subprocess.run(["git", "-C", str(checkout), "config", "user.name", "test"], check=True)
    (checkout / "x").write_text("x", encoding="utf-8")
    subprocess.run(["git", "-C", str(checkout), "add", "x"], check=True)
    subprocess.run(["git", "-C", str(checkout), "commit", "-qm", "test"], check=True)
    with pytest.raises(FdeFixtureValidationError, match=PROTOCOL_CONTRACT_BASELINE_COMMIT) as exc:
        FdeTrial001FixtureAdapter(checkout)
    assert exc.value.error_code == "REPLAY_DEPENDENCY_MISSING"
