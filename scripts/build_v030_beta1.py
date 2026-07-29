#!/usr/bin/env python3
"""Assemble the immutable v0.3.0-beta.1 public pre-release package.

This wrapper reuses the RC5 integrity-closed assembler while replacing every
release identity field with the approved public Beta identity.  It must be
run only from the exact clean commit selected for the v0.3.0-beta.1 tag.
"""

from __future__ import annotations

import os
import sys

import build_v030_rc5 as assembler


assembler.ZIP_NAME = "observer-v0.3.0-beta.1-win-x64.zip"
assembler.RC_TAG = "V030-BETA1"
assembler.OUTPUT_LABEL = "V030-BETA1_RELEASE"
assembler.PRODUCT_VERSION = "v0.3.0-beta.1"
assembler.BUILD_CHANNEL = "BETA"
assembler.RELEASE_STATUS = "RELEASED"
assembler.DOTNET_SRC = (
    os.environ.get("FSP_DOTNET_ROOT")
    or os.environ.get("DOTNET_ROOT")
    or ""
)


if __name__ == "__main__":
    sys.exit(assembler.main())
