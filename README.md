# Full Spectrum Observer

[English](README.md) · [简体中文](README.zh-CN.md)

> Local-first Observer application for reproducible evidence, audit traces and bounded human review.

[![Full Spectrum three entries and three core components](https://github.com/full-spectrum-lab/full-spectrum-commons/blob/main/diagrams/architecture/three-entry-three-core-components-zh-v10.png?raw=1)](https://github.com/full-spectrum-lab/full-spectrum-commons/blob/main/docs/three-entry-three-core-components.md)

**Where Observer fits:** Protocol defines subjects and governance contracts; Engine provides deterministic analysis; Observer connects authorized local facts to evidence, replay and a human decision point. The public Observer line remains observation-only and does not execute final enterprise or production actions.

[![Foundation gates](https://github.com/full-spectrum-lab/full-spectrum-observer/actions/workflows/foundation-gates.yml/badge.svg)](https://github.com/full-spectrum-lab/full-spectrum-observer/actions/workflows/foundation-gates.yml)
[![Release](https://img.shields.io/badge/release-v0.2.0--alpha.2-orange)](https://github.com/full-spectrum-lab/full-spectrum-observer/releases/tag/v0.2.0-alpha.2)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MulanPSL--2.0%20OR%20Apache--2.0-blue)](LICENSE)

## Release truth

| Line | Status | Scope |
| --- | --- | --- |
| [`v0.2.0-alpha.2`](https://github.com/full-spectrum-lab/full-spectrum-observer/releases/tag/v0.2.0-alpha.2) | **Public pre-release** | Engine v1.0/v1.5 compatibility adapter over the Foundation Kernel. |
| `v0.3.0-beta` | **In development — not released** | Local single-user Operator Console. |
| `v0.4`–`v1.0` | **Designed — not implemented** | Scenario Packs, enterprise node, multi-principal service and real-organization validation. |

The current GitHub Release is a source release. A roadmap or Wiki document is not an installable client and is not presented as a shipped capability.

## What is implemented

The released Foundation/compatibility line provides:

- a pinned .NET 10 `win-x64` build baseline;
- immutable runtime snapshots and a native SQLite evidence core;
- a process-isolated Engine Facade with a private Python 3.11 runtime;
- preservation of version, Profile, UNKNOWN, reason-code, Replay and Audit semantics across the Engine v1.0/v1.5 compatibility layer;
- deterministic CASE005 fixtures, evidence manifests and offline verification gates;
- dual licensing under `MulanPSL-2.0 OR Apache-2.0`.

The latest `main` Foundation-gate workflow is the public CI source of truth. Historical candidate-branch reports remain available in the repository for audit, but do not override the release table above.

## Architecture boundary

```text
Observer application (.NET 10)
  → Application / Evidence Core
  → Observer.EngineFacade
  → pinned private Python worker
  → fixed Engine contract
```

Only `Observer.EngineFacade` may start the Engine worker. Observer does not reimplement FSHI, Risk, ESS, Gate, UNKNOWN, Explanation or Runestone calculations. It records and presents results; it does not certify, authorize or execute final enterprise actions.

## Reproduce the source gates

Prerequisites are intentionally pinned and are not downloaded by the scripts:

- .NET SDK `10.0.301`;
- target `net10.0`, RID `win-x64`;
- private Python 3.11 and pinned native SQLite where required;
- Engine and schema identities recorded in repository lock files.

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG1
```

For gates that require private runtime paths:

```powershell
$env:FSP_PRIVATE_PYTHON = "C:\path\to\private-python-3.11\python.exe"
$env:FSP_SQLITE_NATIVE_DIR = "C:\path\to\pinned-sqlite-win-x64"
pwsh ./scripts/test.ps1 -Gate IG3
pwsh ./scripts/test.ps1 -Gate IG4
```

## Evidence and documentation

- [Releases](https://github.com/full-spectrum-lab/full-spectrum-observer/releases)
- [Foundation gate workflow](https://github.com/full-spectrum-lab/full-spectrum-observer/actions/workflows/foundation-gates.yml)
- [Evidence directory](evidence/)
- [Baseline documents](docs/baselines/)
- [Architecture decisions and documentation](docs/)
- [Source package manifest](SOURCE_PACKAGE_MANIFEST.json)
- [Security policy](SECURITY.md)
- [Contributing](CONTRIBUTING.md)
- [Three entry paths and three core components](https://github.com/full-spectrum-lab/full-spectrum-commons/blob/main/docs/three-entry-three-core-components.md)
- [Synthetic industrial evidence-gap case](https://github.com/full-spectrum-lab/full-spectrum-enterprise-governance/tree/main/cases/industrial-tightening-evidence-gap)

## Ecosystem

| Repository | Role |
| --- | --- |
| [full-spectrum-protocol](https://github.com/full-spectrum-lab/full-spectrum-protocol) | Protocol schemas, specifications and conformance rules |
| [full-spectrum-engine](https://github.com/full-spectrum-lab/full-spectrum-engine) | Deterministic local-first governance runtime |
| [full-spectrum-enterprise-governance](https://github.com/full-spectrum-lab/full-spectrum-enterprise-governance) | Enterprise cases, adapters and review workflows |
| [full-spectrum-commons](https://github.com/full-spectrum-lab/full-spectrum-commons) | Evidence status, diagrams, research and public navigation |

## License

Choose either `MulanPSL-2.0` or `Apache-2.0`. Third-party components retain their own licenses; release packages must include their exact SBOM and notices.
