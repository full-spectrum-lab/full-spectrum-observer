"""Make the ``tests`` directory a package so pytest imports the nested conftest
as ``tests.compat.conftest`` rather than ``compat.conftest``. Without this, the
``tests/compat`` directory would shadow the real ``src/compat`` package and break
``import compat``. (Harmless for the .NET test projects in this repo.)
"""
