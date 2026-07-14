# EvaluationEvent / ReplayBundle 与 Observation 所有权规则

**文档落点**：`docs/compat/ownership-rules-evaluationevent-replaybundle.md`（T06 产出）
**权威基线**：`2_Engine兼容与版本协商策略.md`（v1.4 EvaluationEvent/ReplayBundle；v1.5 Review）
**代码入口**：`src/compat/adapter_result.py`（`EvaluationEventRef` / `ReplayBundleRef` / `ReviewRef` / `AdapterResult.verify_refs_resolvable`）

> 本文档是 v0.2 双基线 Adapter 层关于「Engine 事件所有权」的权威规则说明，继承自 Engine v1.4 / v1.5 的 replay 红线，由 Observer 兼容层严格执行。

---

## 1. 核心原则：引用传递，所有权不并入

`EvaluationEvent`、`ReplayBundle`、`Review` 由 **Engine** 产生并拥有。Observer 兼容层（Adapter）仅做：

1. **投影保真**：把 v1.5 Envelope 内的相关节段忠实地复制到 `ObserverEnvelope`（供审计透明），但**不对其进行任何治理计算**；
2. **引用提取**：以 `EvaluationEventRef` / `ReplayBundleRef` / `ReviewRef` 三个轻量引用对象暴露给上层，**不把 Engine 事件并入 Observer 的 Observation 所有权**。

即：`AdapterResult` 同时持有 `raw_envelope`（Engine 原始输出）与 `projected_envelope`（Observer 投影），二者均为存证；事件本身的所有权始终留在 Engine 侧。

---

## 2. 引用结构

| 引用 | 关键字段 | 说明 |
|---|---|---|
| `EvaluationEventRef` | `event_id`, `event_digest`, `bundle_ref` | 指向一个真实 EvaluationEvent |
| `ReplayBundleRef` | `bundle_id`, `capability_level`, `missing_deps` | 指向回放包；缺失依赖**显式**列出（不静默补齐） |
| `ReviewRef` | `review_id`, `original_event_ref` | 指向被复核事件的真实 id |

---

## 3. 可解析性校验（伪造即判失败）

`AdapterResult.verify_refs_resolvable()` 在 `EngineFacade` 编排末端执行，判定每个引用是否指向 Engine 原始输出中**真实存在**的实体：

- `EvaluationEventRef.event_id` 必须存在于 `raw_envelope.payload.evaluation_events[].event_id`；
- `ReplayBundleRef.bundle_id` 必须存在于 `raw_envelope.payload.replay_bundle.bundle_id`；
- `ReviewRef.original_event_ref` 必须指向一个真实存在的 `event_id`，**为空或指向不存在的事件即判伪造**。

任一不可解析 → `EngineFacade.execute` 以结构化 `UnsupportedVersionError(reason_code=REFERENCE_UNRESOLVABLE)` 失败闭环（fail-closed），**绝不写半截结果、绝不静默放行**。

> 继承 Engine v1.5 replay 红线 #8：禁止编造空引用或虚构事件；`original_event_ref` 必须可解析。

---

## 4. 与反模式红线的对应

| 红线 | 本规则落点 |
|---|---|
| R03 所有权不并入 Observation | §1 引用传递，事件所有权留在 Engine |
| R08 不伪造证据 | §3 `verify_refs_resolvable` 强制 `original_event_ref` 可解析 |
| R11 不重造治理算法 | Adapter 只读 Engine 输出投影，不计算 FSHI/Risk/ESS/Gate |
| R05 固定 digest | 引用携带 `event_digest` / `canonical_digest`，可重放审计 |
| R12 不改写历史审计 | `AdapterResult` 仅追加新 Observation，不回写 Engine 事件 |

---

## 5. 测试用例覆盖

- `tests/compat/test_ownership_rules.py`：`test_v15_references_present_but_not_owned`、`test_v1_has_no_refs_to_own`、`test_fabricated_reference_is_rejected`、`test_facade_rejects_fabricated_reference`、`test_observed_subject_is_not_auth_principal`。
- `tests/compat/test_v15_adapter.py`：`test_v15_references_extracted_not_owned`、`test_v15_deterministic_rerun`。
- Fixture：`tests/compat/fixtures/v1.5_case005/`（含 Envelope + Eval/Replay/Review 引用 + EvidenceEnvelope）、`tests/compat/fixtures/neg/dependency_not_replayable.json`（伪造引用负例）。
