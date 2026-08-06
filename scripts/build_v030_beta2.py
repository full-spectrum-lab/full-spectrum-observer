#!/usr/bin/env python3
"""Assemble the v0.3.0-beta.2 candidate pre-release package.

This wrapper reuses the RC5 integrity-closed assembler while replacing every
release identity field with the v0.3.0-beta.2 identity.  It must be run only
from the exact clean Candidate B commit selected for v0.3.0-beta.2.

The immutable v0.3.0-beta.1 assembler (``build_v030_beta1.py``) is a historical
anchor bound to the ``v0.3.0-beta.1`` tag and is deliberately left untouched;
beta.2 is assembled by this additive script instead.
"""

from __future__ import annotations

import os
import sys

import build_v030_rc5 as assembler


assembler.ZIP_NAME = "observer-v0.3.0-beta.2-win-x64.zip"
assembler.RC_TAG = "V030-BETA2"
assembler.OUTPUT_LABEL = "V030-BETA2_RELEASE_CANDIDATE"
assembler.PRODUCT_VERSION = "v0.3.0-beta.2"
assembler.BUILD_CHANNEL = "BETA"
assembler.RELEASE_STATUS = "NOT_RELEASED"
assembler.DOTNET_SRC = (
    os.environ.get("FSP_DOTNET_ROOT")
    or os.environ.get("DOTNET_ROOT")
    or ""
)


if __name__ == "__main__":
    sys.exit(assembler.main())
