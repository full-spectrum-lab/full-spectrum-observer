"""Shared pytest fixtures for the Compatibility Adapter test-suite."""

from __future__ import annotations

import json
import os
import sys

# Ensure the standalone ``src/compat`` package is importable regardless of how
# pytest resolves the rootdir (the repo is otherwise a .NET solution).
_REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
_SRC = os.path.join(_REPO, "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from typing import Any, Dict

import pytest

from compat.engine_facade import EngineFacade
from compat.engine_v1_adapter import EngineV1Adapter
from compat.engine_v15_adapter import EngineV15Adapter
from compat.runtime_snapshot import RuntimeConfigurationSnapshot

_FIXTURE_ROOT = os.path.join(os.path.dirname(__file__), "fixtures")


def load_fixture(rel_path: str) -> Dict[str, Any]:
    """Load a JSON fixture relative to ``tests/compat/fixtures``."""
    with open(os.path.join(_FIXTURE_ROOT, rel_path), "r", encoding="utf-8") as handle:
        return json.load(handle)


@pytest.fixture
def fixture_path() -> str:
    return _FIXTURE_ROOT


@pytest.fixture
def load_json():
    """Return a callable that loads a fixture JSON by relative path."""

    def _load(rel_path: str) -> Dict[str, Any]:
        return load_fixture(rel_path)

    return _load


@pytest.fixture
def snapshot_v1_0() -> RuntimeConfigurationSnapshot:
    return RuntimeConfigurationSnapshot.frozen_v1_0_0()


@pytest.fixture
def snapshot_v1_5() -> RuntimeConfigurationSnapshot:
    return RuntimeConfigurationSnapshot.frozen_v1_5_0()


@pytest.fixture
def v1_facade() -> EngineFacade:
    """Facade wired with the v1.0.0 adapter only."""
    facade = EngineFacade()
    facade.register_adapter("1.0.0", EngineV1Adapter())
    return facade


@pytest.fixture
def v15_facade() -> EngineFacade:
    """Facade wired with the v1.5.0 adapter only."""
    facade = EngineFacade()
    facade.register_adapter("1.5.0", EngineV15Adapter())
    return facade


@pytest.fixture
def dual_facade() -> EngineFacade:
    """Facade wired with both adapters (full dual-baseline support)."""
    facade = EngineFacade()
    facade.register_adapter("1.0.0", EngineV1Adapter())
    facade.register_adapter("1.5.0", EngineV15Adapter())
    return facade
