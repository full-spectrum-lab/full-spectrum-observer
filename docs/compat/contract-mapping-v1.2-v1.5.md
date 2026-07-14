# Observer v0.2.0-alpha 契约映射矩阵 v1.2–v1.5

**文档落点**：`docs/compat/contract-mapping-v1.2-v1.5.md`（T05 产出，VG2 必备契约文档）
**权威口径**：`2_Engine兼容与版本协商策略.md` + ADR-001
**数据落点**：`src/compat/contract_map_v1.2_v1.5.json`
**代码入口**：`src/compat/compatibility_matrix.py`（`CompatibilityMatrix` / `ContractMapping`）

> ⚠️ **契约版本口径（ADR-001，唯一权威）**：Envelope = **v1.2**；Profile / Scenario / UNKNOWN / hard gate = **v1.3**；EvaluationEvent / ReplayBundle = **v1.4**；Subject Declaration / Operator-Service RBAC / Desensitization / Review / Resilience / Connector = **v1.5**。**不存在任何 "v1.1 Subject" 标注**——需求/测试/发布文档中把 Subject 标为 v1.1 属版本号漂移（F1），已统一为 v1.5。

---

## 1. 能力 ↔ Engine 契约版本 ↔ Observer 字段 ↔ 保真规则

| 能力 | Engine 契约版本 | Observer 字段 | 保真规则 | 支持 |
|---|---|---|---|---|
| Envelope | v1.2 | `raw_envelope` + `projected_envelope`（obs-1.0） | 输入输出双 Schema 校验；保留 raw / canonical / output 三引用 | ✅ |
| Profile | v1.3 | `profile_scenario.profile` | 固定 id/version/digest；不降级 | ✅ |
| Scenario | v1.3 | `profile_scenario.scenario_ref` | 固定 scenario_ref；不降级 | ✅ |
| UNKNOWN | v1.3 | `unknowns` / 各节 `== UNKNOWN` | 显式 UNKNOWN；静默丢失即阻断 | ✅ |
| HardGate | v1.3 | `hard_gate` | hard gate 不被综合分数覆盖（FR-PF-005） | ✅ |
| EvaluationEvent | v1.4 | `evaluation_events` + `EvaluationEventRef` | 引用传递；所有权不并入 Observation | ✅ |
| ReplayBundle | v1.4 | `replay_bundle` + `ReplayBundleRef` | 引用传递；依赖缺失显式传递 | ✅ |
| SubjectDeclaration | **v1.5** | `subject_declaration` | 复用声明；不重造主体判断内核；**非认证主体** | ✅ |
| RBAC | v1.5 | `rbac` | 仅 Operator/Service Principal；身份面与 ObservedSubject 分离 | ✅ |
| Desensitization | v1.5 | `desensitization` | 保留映射可见性 | ✅ |
| Review | v1.5 | `review` + `ReviewRef` | `original_event_ref` 可解析；不伪造复核证据 | ✅ |
| Resilience | v1.5 | `resilience` | 错误码/幂等/超时/健康/指标映射可测试 | ✅ |
| Connector | v1.5 | `connector` | 写回默认 OFF；契约导出不误写为已执行业务 | ✅ |

---

## 2. 矩阵健康度判定

`CompatibilityMatrix.all_green()` 当且仅当全部映射 `supported == true` 时返回 `True`。当前数据 **13 项能力全部 `supported`，覆盖度 100%**。

- `covers(capability, contract_version)`：命中受支持映射返回 `True`。
- `unsupported()`：返回所有未支持映射（当前为空）。
- `coverage()`：支持数 / 总数（当前 1.0）。

发布门禁：兼容矩阵必须 **100% 通过**，否则阻断发布。

---

## 3. 与红线（R01–R15）的对应关系

| 红线 | 对应映射/规则 |
|---|---|
| R04 协商失败结构化返回 | `version_resolver.UnsupportedVersionError`（矩阵外的版本直接 fail-closed） |
| R05 固定 Engine tag/commit/digest | `runtime_snapshot` 冻结；矩阵不引入浮动版本 |
| R06 ObservedSubject 非认证主体 | SubjectDeclaration 映射标注「非认证主体」 |
| R07 Connector 写回默认 OFF | Connector 映射保真规则 |
| R08 `original_event_ref` 可解析 | Review 映射保真规则 |
| R09/R10 UNKNOWN / hard gate 不降级 | UNKNOWN 与 HardGate 两行映射 |
| R11 不重造治理算法 | 全部为「投影/引用」，无任何 FSHI/Risk/Gate 计算 |

---

## 4. 演进与回填

- v1.5.0 封板后，`src/compat/contract_map_v1.2_v1.5.json` 中的 `engine_digest` 占位值（见 `runtime_snapshot.py` 注释「待 v1.5 正式封板后回填真实 digest」）需回填为真实 digest，并重新运行 `pytest tests/compat` 确认兼容矩阵与双存证仍全绿。
- 新增 Engine 契约版本时，仅需在该 JSON 追加 `ContractMapping` 行并刷新本文档，无需改动 `CompatibilityMatrix` 代码。
