# WorkBuddy v0.4 re-audit prompt

你是独立复验者 WorkBuddy。请对 Observer v0.4 最终提交和最终内部候选 ZIP 做只读、可重复、证据导向的复验。不要修改仓库、不要生成或上传真实数据、不要 push/merge/release/deploy，也不要把本机 `C:\obs-v04-real-pilot` 的原始材料复制到仓库或报告中。

## 审计对象

1. 仓库：`C:\Users\wangjian0926\Desktop\codex专属仓库\worktrees\observer-v04-closure`
2. 审计目标：用户随后提供的最终 `HEAD`（记录完整 SHA-1、分支和工作树状态）。
3. 安装包：用户随后提供的 `observer-v0.4.0-beta-internal-candidate-<commit>.zip`、其 SHA-256 和解压目录。
4. 真实窄样本仅可按本机授权边界读取：`C:\obs-v04-real-pilot\RP-001`。不得上传、改写、扩展或公开其中任何原文。

## 必须先记录的边界

- 当前裁定目标是 `INTERNAL_CANDIDATE / NO_GO_UNTIL_EXTERNAL_GATES`，不是 v0.4 Release。
- v0.4.5 Cloud Skill、v0.5、v0.6、RBAC、远程监听、自动业务动作和 CASE008 四层系统均不属于本次审计。
- `PASS` 只表示仓库或本机结构化管线证据；`EXTERNAL` 表示仍需独立人工/目标用户证据；不能把二者合并。
- RP-001 只验证显式结构化 `assertion_key` + `polarity` + `evidence_role`。不得声称支持任意自然语言语义判断。

## 源码与测试复验

在固定 .NET SDK、Python 3.12、仓库锁定依赖和 SQLite 原生库下执行并保存原始日志：

1. Release 构建，必须 `0 warnings / 0 errors`。
2. 完整 .NET unit、contract、security、architecture、integration 测试。
3. `scripts/test-v04.ps1` 聚合门，确认 v0.4 单元过滤、真实 Engine v1.5 集成、Python contract gate 和 git 漂移检查均通过。
4. `git diff --check`，确认审计期间工作树无变化。
5. 逐项阅读并核对：Scenario Pack Loader、RequestFactory、Runner CLI、SQLite migration、candidate adapter、Active-only preflight、batch claim affected-row guards、audit chain。

## 外部结构化输入复验

使用 RP-001 Pack 与 Owner trust store，在单独新建的本机 data directory 中运行：

```powershell
$env:FSP_PRIVATE_PYTHON = "<pinned-private-python>"
observer.cmd scenario-pack-run `
  --pack "C:\obs-v04-real-pilot\RP-001-pack" `
  --input "RP-001-pack/golden/rp001.input.json" `
  --input-root "C:\obs-v04-real-pilot" `
  --trust-store "RP-001/owner-rp001-trust-store.json" `
  --trust-store-sha256 a26a2049e786ccbf2a47af3061a457041a6fe1043711b27963bcd74774fdc6e2 `
  --data-dir "<fresh-data-dir>" `
  --authorization-ref sha256:7054e2798f9420d7080109135a82baf247f0b32ac225d4020ce634664e2f072f `
  --redaction-ref sha256:2fd848478906176a8199e8e3275384125c2426f0fe5030cf39ac3bda5d5589bc `
  --deletion-ref sha256:9acab69770c7c61f17e0fbdb24b28be3be7a954179da48080a381214cc7931d1 `
  --json
```

运行两次。第一次必须 `COMPLETED`、`candidate_only=true`、`candidate_count=1`、
`minority_evidence_survived=true`、`authorized_action=false`，并显示
`opposition_not_established:scan_manifest_available`。第二次必须 `idempotent=true`。

用 SQLite 只读查询确认：候选账本有一行，`minority_evidence_refs_json` 含
`owner-correction@2026-08-20`，`missing_context_json` 含
`opposition_not_established:scan_manifest_available`，`authorized_action=0`；Engine
`conflict_observations` 与 Observer 候选账本分离。

## 必须执行的负向用例

- 信任库 SHA-256 错误；非 `OWNER_LOCAL_PILOT:*` purpose；Pack digest 或签名篡改。
- 输入缺少 `assertion_key`、`polarity` 或 `evidence_role`；非法枚举；重复 source ref；越界路径。
- 只有同向 `AFFIRM`、没有 `DENY`：不得制造冲突，必须保留 `UNKNOWN`。
- 改写候选账本：UPDATE 和 DELETE 都必须被不可变触发器拒绝。
- 重复 idempotency key 但输入/治理摘要不同：必须失败关闭并报告冲突。

## 报告格式

报告必须包含：审计对象完整 SHA、环境、命令和退出码、原始日志路径及 SHA-256、逐项 PASS/FAIL/EXTERNAL/INFO 表、负向用例结果、发现按严重度排序、与上一份 WorkBuddy 报告的提交时序差异、是否修改工作树，以及最终裁定。

明确写出：本次通过的是结构化管线与包完整性，不是自然语言真值判断、外部试点验收或 Release 授权。若任何命令无法运行，写明环境阻断，不得猜测为 PASS。
