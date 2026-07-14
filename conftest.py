"""Repo-root pytest configuration for the Observer Compatibility Adapter layer.

Ensures the standalone Python ``src/compat`` package is importable (the repo is
otherwise a .NET solution with no Python tooling). This keeps
``python -m pytest tests/compat`` runnable under the dedicated venv without
modifying the .NET build or adding a conflicting pyproject.toml.
"""

import os
import sys

_SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)
