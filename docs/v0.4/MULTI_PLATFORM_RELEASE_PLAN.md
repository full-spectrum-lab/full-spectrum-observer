# v0.4 Multi-Platform Release Plan

## Scope

Observer releases use one source version with separate, platform-specific artifacts. A package must not contain native binaries for unrelated operating systems or CPU architectures.

## Artifact matrix

| Artifact suffix | Target platform | Status |
|---|---|---|
| `win-x64` | Windows x64 | First priority; current internal candidate target |
| `win-arm64` | Windows ARM64 | Planned |
| `linux-x64` | Linux x64 | Planned |
| `linux-arm64` | Linux ARM64 | Planned |
| `osx-x64` | macOS Intel | Planned |
| `osx-arm64` | macOS Apple Silicon | Planned |

Each artifact is built with its target runtime identifier and contains only that target's native assets. Portable artifacts may embed the pinned .NET, Python, and SQLite runtimes; framework-dependent variants may be added later.

## Evidence requirements

Every artifact receives its own:

- release manifest with operating system, architecture, runtime identifier, source commit, and build timestamp;
- `SHA256SUMS.txt` and SPDX SBOM;
- platform-specific launcher and verification instructions;
- startup, version, Golden Case, idempotency, and Engine integration evidence.

Evidence is not shared across platforms. An untested platform remains `NOT_EXECUTED` and cannot be represented as passed by another platform's result.

## Release layout

One version Release may contain all completed platform artifacts. The Release description must identify each file's target platform and verification status. Automatically generated source archives are not installation packages.

The current `v0.4.0-beta-internal-candidate-2492715` package remains frozen as an internal Windows candidate. This plan applies to the next rebuilt candidate and does not alter that package or its hash.
