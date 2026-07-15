# Trace Link Matrix — v0.3.0-beta Observer Console

Maps each design-authority artifact (研发任务书 / R1 详细设计) to the concrete source file
delivered on branch `feature/v0.3-observer-console`. Authoring environment had **no .NET SDK**,
so the build/warning verification is delegated to the integrator (see report).

## T-DataModel (研发任务书 §4.2)

| Design artifact | Real file(s) |
| --- | --- |
| §4.2 DDL — 10 tables | `src/Observer.Store/Data/Migrations/Init.sql` |
| §4.2 classDiagram — 12 model classes | `src/Observer.Contracts/Models/` — `ObservedSubject.cs`, `SubjectVersion.cs`, `KnowledgeSource.cs`, `KnowledgeSourceVersion.cs`, `RawAnalysisInput.cs`, `AnalysisTask.cs`, `AnalysisTaskStatus.cs`, `AnalysisResult.cs`, `ConflictObservation.cs`, `RuntimeSnapshot.cs`, `EvidenceBundle.cs`, `AuditRecord.cs` |
| ADR-001 versioning (append-only audit; immutable Active; partial unique index) | `src/Observer.Store/ObserverStore.cs` (`Activate*SubjectVersionAsync`, `Retire*SubjectVersionAsync`, `AppendAuditAsync` INSERT-only) + `ux_*_active` partial indexes in `Init.sql` |
| Red line #1 (no login/auth on ObservedSubject) | `ObservedSubject.cs` (context-only fields; no credential members) |
| Red line #7 (audit_records append-only, Active rows not UPDATE-able) | `ObserverStore.cs` + `Init.sql` CHECK/partial-index guards |

## T-EngineFacade (研发任务书 §4.3 + R1-B)

| Design artifact | Real file(s) |
| --- | --- |
| §4.3 request/response field tables (exact) | `src/Observer.EngineFacade/EngineV15Contract.cs` (`EngineRequest`, `EngineResponse`, sub-objects) |
| §4.3 engine_version == "1.5.0" enforcement + response digest integrity | `src/Observer.EngineFacade/EngineFacade.cs` (`AnalyzeAsync` request/response validation, fail-closed) |
| Process invocation of pinned Engine v1.5.0 (single local operator) | `src/Observer.EngineFacade/EngineFacade.cs` + `EngineV15Options.cs` + `EngineV15Composition.cs` |
| R1-B §10.6 pinned values (tag/commit/artifact_digest/adapter/schema/matrix) | `src/Observer.EngineFacade/EngineV15Contract.cs` (consts) + `src/Observer.Host.Web/appsettings.json` |
| `RetentionMode` enum (SANITIZED_PERSISTENT / FULL_LOCAL / EPHEMERAL) | `src/Observer.EngineFacade/RetentionMode.cs` |
| IntakeAdapter — structure validation only (no governance judgement) | `src/Observer.EngineFacade/IntakeAdapter.cs` |
| OutputAdapter — pass-through (no recompute/downgrade/merge) | `src/Observer.EngineFacade/OutputAdapter.cs` |
| ADR-002 EngineFacade contract; ADR-003 anchor v1.5 | enforced across `EngineV15Contract.cs` + `EngineFacade.cs` |
| Red line #8 (replay_ref / evidence_digest not forged; missing → error) | `EngineFacade.cs` (throws `ContractViolationException` when missing); `OutputAdapter.cs` (verbatim); `ObserverStore.cs` (`InputDigest` must equal task `content_digest`) |
| Red line #9 (Observer does NOT recompute governance) | `OutputAdapter.cs` + `EngineFacade.cs` (verbatim pass-through) |

## T-Pages×7 (R1-C)

| Design artifact | Real file(s) |
| --- | --- |
| R1-C 7 pages (component tree / state machine / empty+error states / a11y) | `src/Observer.Host.Web/Pages/` — `Home.razor`, `SubjectManagement.razor`, `KnowledgeManagement.razor`, `NewAnalysis.razor`, `AnalysisRecords.razor`, `AuditEvidence.razor`, `SystemInfo.razor` |
| Services: Orchestrator / SubjectCatalog / KnowledgeCatalog / AnalysisWorkspace / AuditViewer / SystemDiagnostics | `src/Observer.Host.Web/Services/*.cs` |
| Loopback-only binding (never 0.0.0.0) | `src/Observer.Host.Web/Program.cs` (`UseKestrel` → `ListenLocalhost(5180)`) + `SystemInfo.razor` |

## Notes
- `engine_artifact_digest` is an explicit **PLACEHOLDER** constant in `EngineV15Contract.cs`
  (not fabricated); must be filled from the published artifact before GO-6.
- `schema_version` / `schema_digest` are **computed** from `Init.sql` via `SchemaDefinition.cs`
  (sha256 of the embedded resource), satisfying "compute from Observer-controlled files".
