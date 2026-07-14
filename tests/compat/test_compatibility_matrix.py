"""T05 — Compatibility matrix coverage and all-green judgement (ADR-001口径)."""

from __future__ import annotations

from compat.compatibility_matrix import CompatibilityMatrix

# ADR-001: Subject Declaration is v1.5, NOT v1.1. Envelope is v1.2, etc.
_FORBIDDEN_VERSION_TOKENS = ("v1.1",)


def test_default_matrix_all_green():
    matrix = CompatibilityMatrix.default()
    assert matrix.all_green() is True
    assert matrix.coverage() == 1.0
    assert matrix.unsupported() == []


def test_explicit_load_all_green():
    matrix = CompatibilityMatrix.load()
    assert matrix.all_green() is True


def test_coverage_for_known_capabilities():
    matrix = CompatibilityMatrix.default()
    assert matrix.covers("Envelope", "v1.2") is True
    assert matrix.covers("Profile", "v1.3") is True
    assert matrix.covers("EvaluationEvent", "v1.4") is True
    assert matrix.covers("SubjectDeclaration", "v1.5") is True
    # Subject is NOT v1.1.
    assert matrix.covers("SubjectDeclaration", "v1.1") is False


def test_no_v1_1_subject_drift():
    """Hard ADR-001 guard: no mapping labels Subject as v1.1."""
    matrix = CompatibilityMatrix.default()
    for mapping in matrix.mappings:
        assert mapping.engine_contract_version not in _FORBIDDEN_VERSION_TOKENS
        # The capability literally named Subject* must map to v1.5.
        if mapping.capability.startswith("Subject"):
            assert mapping.engine_contract_version == "v1.5"


def test_lookup_returns_mapping():
    matrix = CompatibilityMatrix.default()
    m = matrix.lookup("Connector", "v1.5")
    assert m is not None
    assert m.supported is True
    assert "OFF" in m.fidelity_rule
