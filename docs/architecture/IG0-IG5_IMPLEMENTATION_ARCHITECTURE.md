# Observer v0.1 Foundation Kernel IG0–IG5 实现技术架构

文档状态：CURRENT IMPLEMENTATION BASELINE  
对应代码：Gitee commit `f2fba54`  
日期：2026-07-13

## 1. 文档目的

本文描述正式仓库中已经实现并实际执行的 IG0–IG5 架构。它回答组件在哪里、职责是什么、数据如何流动、依赖如何固定、测试如何对应代码。本文不是未来完整产品架构，也不代表 IG6–IG8 或 v0.1.0-alpha 已发布。

## 2. 系统定位

Foundation Kernel 是 Observer 的最小确定性执行闭环，面向开发、测试与架构验证。它在本地断网环境中接收固定 CASE，经过输入、转换、双校验、运行时快照、Engine、输出、证据和审计，形成可查询、可校验的 Observation。

Observer 不复制 Engine 的 FSHI、Risk、ESS、Gate、UNKNOWN、Explanation 或 Runestone 算法。只有 `Observer.EngineFacade` 可以启动固定 Python Worker。

## 3. 运行时架构

```text
Observer.Host.Cli
  │
  ▼
Observer.Execution ───────────────┐
  │ Input / Adapter / Validation  │
  │ Runtime Snapshot / Output     │
  ▼                               ▼
Observer.EngineFacade         Observer.Evidence
  │ Python child process          │ SQLite + Artifact Store
  ▼                               │ Append-only Audit
Pinned Python Worker              │
  │                               │
  ▼                               │
Vendored Engine v1.0.0            │
  └──────── structured output ────┘
```

公共合同、canonicalization、Schema 和 reason code 由 `Observer.Contracts` 提供；用例端口由 `Observer.Application` 提供。

## 4. 项目与依赖方向

| 项目 | 职责 | 允许依赖 |
|---|---|---|
| Observer.Contracts | wire model、Schema、canonical JSON、digest、reason code、状态机 | 无 Observer 项目依赖 |
| Observer.Application | Engine/Evidence/Clock/ID 等端口 | Contracts |
| Observer.Execution | 输入、Adapter、校验、Snapshot、用例和输出组装 | Application、Contracts |
| Observer.EngineFacade | Worker 完整性、子进程、超时、输出上限、错误映射 | Application、Contracts |
| Observer.Evidence | SQLite、Artifact、Observation、Operation、Idempotency、Audit | Application、Contracts |
| Observer.Host.Cli | CLI 参数与 composition root | 上述全部运行组件 |

依赖方向由 `scripts/verify-architecture.py` 校验。Engine Facade、Evidence 和 Execution 之间没有横向项目引用，由 Host 统一组装。

## 5. 端到端执行链

```text
INTAKE
→ ADAPTER
→ SCHEMA_VALIDATION
→ GOVERNANCE_VALIDATION
→ SNAPSHOT
→ ENGINE_FACADE
→ ENGINE
→ OUTPUT
→ OBSERVATION
→ AUDIT
```

### 5.1 Intake

`FoundationInputIntake` 接收内置 CASE 或受控文件输入，检查允许根目录、文件大小、存在性、JSON 和输入 digest。失败返回稳定 reason code，不进入 Engine。

### 5.2 Adapter

`FoundationScenarioAdapter` 只做 CASE005 场景到 canonical context 的确定性映射。它不计算最终风险和 Gate。

### 5.3 双校验

`FoundationValidationPipeline` 分离 Schema Validation 与 Governance Validation。格式错误和治理上下文不足使用不同状态与 reason code。

### 5.4 Runtime Snapshot

`RuntimeConfigurationResolver` 生成不可变 Snapshot，固定 Engine、Schema、Case Pack、Scenario、Profile/Policy/Knowledge 引用、serialization 和 SHA-256。禁止 `latest/current` 等浮动依赖。

### 5.5 Engine Facade

`PythonWorkerEngineFacade`：

- 验证 `engine/worker.lock.json` 与每个 vendored 文件摘要；
- 使用绝对 Python 路径启动单次子进程；
- stdin/stdout 采用单行 JSON；stderr 仅诊断；
- 清除代理环境并设置 `NO_PROXY=*`；
- 限制响应和 stderr 大小；
- 区分取消、超时、Worker 缺失、版本不匹配和 Engine 错误；
- 不调用 Engine SQLite 或 FastAPI。

固定 Engine 为 v1.0.0、commit `09062bae2c7608bda79ee4bfde5779109e8e6197`。

### 5.6 Output

`GovernanceOutputAssembler` 保留 Engine 原始结构和 digest，并增加 Observer 自有边界：

```text
observer_only = true
certified = false
authorized = false
active_external = false
```

### 5.7 Evidence

Evidence Core 使用单个本地 SQLite 数据库和 content-addressed Artifact Store：

- Operation 保存执行状态，不等于 Audit；
- Runtime Snapshot 插入后不可修改；
- Observation 指向 Output Artifact 与 Audit head；
- idempotency key 区分首次、处理中重试和 fingerprint 冲突；
- Observation、Artifact 与 Audit 的最终写入在事务边界内完成。

SQLite 通过 C API P/Invoke，正式验证运行时为官方 SQLite 3.50.4 win-x64。

### 5.8 Audit

Audit Event 使用 FS-OBS-CANON-1 canonical serialization、SHA-256、sequence 和 previous hash。首事件使用 GENESIS 语义；事件只追加，数据库触发器拒绝 UPDATE/DELETE；验证器可重新计算整条链。

## 6. 数据所有权

| 对象 | Owner | 持久化 |
|---|---|---|
| Raw Input | Intake | 默认不长期保存 |
| Canonical scenario/context | Execution | 仅按 Snapshot/Artifact 策略保存 |
| Runtime Snapshot | Observer | 不可变 |
| Engine raw output | Engine 语义、Observer 托管 | content-addressed Artifact |
| Observation | Observer | 可查询业务记录 |
| Audit Event | Observer Audit Ledger | 追加写、hash chain |

## 7. 固定工具链

| 依赖 | 固定值 |
|---|---|
| .NET SDK | 10.0.301 |
| Target Framework | net10.0 |
| RID | win-x64 |
| Python | CPython 3.11.9 x64 |
| NumPy | 1.26.4 |
| jsonschema | 4.25.1 |
| SQLite | 3.50.4 win-x64 |
| Engine | v1.0.0 / `09062bae…` |

NuGet 使用无外部 package source 的 locked restore。Python/SQLite 当前用于正式本地 Gate，IG7 将把运行时、wheelhouse、SBOM 和许可证固化进离线包。

## 8. 当前安全边界

- 核心闭环不依赖公网、模型、遥测或 Connector。
- Worker 不继承 HTTP/HTTPS/ALL proxy。
- Engine 无企业凭证、UI 和 Observer 数据库所有权。
- 输出不得宣称 Certified、Authorized 或 ACTIVE_EXTERNAL。
- 日志与错误不得保存完整敏感 Raw Input。
- 本版本只使用 synthetic CASE005，不构成企业数据验证。

## 9. Gate—实现—证据映射

| Gate | 实现重点 | 主要证据 |
|---|---|---|
| IG0 | baseline lock | `evidence/ig0/baseline-verify.json` |
| IG1 | locked build | `evidence/ig1/build-log.txt` |
| IG2 | Contracts/Schema/architecture | `evidence/ig2/*` |
| IG3 | Evidence/SQLite/Audit | `evidence/ig3/reference-validation.json` |
| IG4 | Worker/Engine Facade | `evidence/ig4/worker-smoke.json` |
| IG5 | Execution/CLI/minimum loop | `evidence/ig5/reference-pipeline.json` |

## 10. 尚未完成

- IG6 综合自动化、安全、隐私、并发和故障注入；
- IG7 私有运行时离线包、SBOM、ReleaseManifest、验包与篡改验证；
- IG8 第二干净环境非实现者复现；
- Web Console 和用户手工产品验收；
- Engine v1.4 Compatibility Adapter。

