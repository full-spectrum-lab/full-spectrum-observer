# Observer v0.4 社区外部试点参与者操作手册

[社区任务说明](COMMUNITY_EVIDENCE_TASK.zh-CN.md) · [English task](COMMUNITY_EVIDENCE_TASK.md)

**当前阶段：** `RECRUITMENT_PREPARATION / EXECUTION_NOT_OPEN`

**重要说明：** 本手册提前公开操作纪律；没有 Owner 单独发放的任务单、候选身份和 SHA-256 时，不得自行寻找或运行所谓 v0.4 候选包

## 1. 你将完成的证据链

```text
确认有权接触材料
  -> 检查脱敏是否充分且保留必要语义
  -> 核验候选身份
  -> 运行脱敏窄样本
  -> 人工查看候选、证据和 UNKNOWN
  -> 独立填写使用结论
  -> 执行或确认删除/退出
  -> 只回传表单、摘要和脱敏日志
```

失败、分歧、`REJECT` 和 `UNKNOWN` 都可以成为合格证据。不要为了得到 PASS 修改输入、输出或日志。

## 2. 角色资格

同一人可以兼任独立人工脱敏检查人和真实目标用户，但必须：

1. 不是被测候选的代码实现者；
2. 没有替数据所有者作出本次授权决定；
3. 已获准在本地安全环境查看原始材料与脱敏副本；
4. 披露自己同时承担两个角色；
5. 实际操作候选并独立填写结论。

如果你只能看到脱敏材料，你只能担任目标用户。如果你使用自己的材料并包办所有角色，结果属于 `COMMUNITY_SELF_PILOT`，不属于独立外部验收。

## 3. 执行前必须收到的任务包

正式任务包至少应包括：

- 任务编号、用途、样本范围和到期/退出条件；
- 冻结候选名称、外部 SHA-256 和身份预期；
- 不含私钥的公共信任库或试点专用公钥；
- 已批准 Scenario Pack 的身份和摘要；
- 四份空白证据模板；
- 安全联系人和问题反馈方式。

任务包不得包含 Owner 私钥、生产私钥、未经授权的原始材料、代填的验收结论或要求只报告成功的指令。

## 4. 开始前声明

请在本地证据表中填写：

```text
任务编号：
参与者姓名或可追溯身份：
我是否独立于代码实现者：是 / 否
我是否有权查看原始材料：是 / 否
我是否同时担任脱敏检查人和目标用户：是 / 否
我是否使用自己的材料：是 / 否
我接受只在本地处理材料、不上传原文：是 / 否
```

任一权限问题不清楚时立即停止。

## 5. 建立本地安全工作区

建议目录：

```text
community-pilot-<TASK_ID>/
  package/           冻结候选，只读使用
  source-private/    原始材料，不回传
  sanitized/         脱敏副本
  run-data/          本次运行账本
  evidence-local/    本地证据和日志
  return-package/    最终允许回传的材料
```

安全要求：

- 不用公共网盘、公开聊天群或 GitHub Issue 传输原文；
- 不把私钥放入 `package/`、`return-package/` 或 Git；
- 关闭不必要的自动云同步；
- 原始材料与脱敏副本分别计算 SHA-256，只回传摘要；
- 日志不得包含用户名、绝对路径、邮箱、凭据或原始句子。

计算摘要：

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '<FILE>'
```

## 6. 核对授权与冻结边界

参与者必须核对：

- 数据所有者与授权依据可追溯；
- 用途只限任务单中的窄场景；
- 生效和到期时间有效；
- 样本数量及纳入/排除规则真实；
- Observer、Engine、Pack、Profile 和输入摘要与任务单一致；
- 删除与退出计划已经明确。

如果你不是数据所有者，不得代替数据所有者修改授权状态。授权不成立时记录 `REJECTED_BEFORE_RUN` 并停止。

## 7. 独立人工脱敏检查

在本机比较原始材料与脱敏副本，至少检查：

1. 姓名、手机号、证件号、账号、地址等直接标识符；
2. 职位、日期、地点、罕见事件组合等间接识别风险；
3. API key、token、密码、证书、私钥和内部 URL；
4. 邮箱、Windows 用户目录和文件服务器路径；
5. 附件、元数据、文件名和日志中的身份泄露；
6. `assertion_key`、`polarity`、`evidence_role` 及必要关系是否仍可理解；
7. 脱敏是否改变原意、制造冲突或删除少数证据；
8. 剩余风险是否能在当前用途和期限内接受。

结论只能是：

- `PASS_WITH_RECORDED_LIMITS`；
- `REWORK_REQUIRED`；
- `REJECTED`。

选择后两项时不要运行候选。脱敏报告只记录方法、数量、摘要、风险和结论，不粘贴敏感文本。

## 8. 核验候选身份

只有在收到 Owner 批准的候选后执行：

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '<OBSERVER_ZIP>'
Set-Location '<OBSERVER_ROOT>'
.\observer.cmd version --json
```

将 SHA-256 和版本输出与任务单逐项比较。必须特别检查：

- `implementation_gate`；
- `maturity`；
- `release_authorized`；
- Observer source commit；
- Engine version 与 commit。

任一不一致时停止并报告 `PACKAGE_IDENTITY_MISMATCH`。不要从第三方网盘、聊天记录或非项目发布位置寻找替代包。

## 9. 运行窄场景

任务单会给出精确路径和摘要。命令结构如下：

```powershell
.\observer.cmd scenario-pack-run `
  --pack '<PACK_PATH>' `
  --input '<SANITIZED_INPUT_JSON>' `
  --input-root '<PILOT_ROOT>' `
  --trust-store '<PUBLIC_TRUST_STORE_JSON>' `
  --trust-store-sha256 '<TRUST_STORE_SHA256>' `
  --authorization-ref 'sha256:<AUTHORIZATION_DIGEST>' `
  --redaction-ref 'sha256:<REDACTION_REPORT_DIGEST>' `
  --deletion-ref 'sha256:<DELETION_PLAN_DIGEST>' `
  --data-dir '<RUN_DATA_DIR>' `
  --batch-id '<TASK_SPECIFIC_BATCH_ID>' `
  --json
```

三个治理引用目前只是摘要格式引用，Runner 不会替你验证授权、脱敏和删除文件内容。参与者必须人工核对。

相同输入和 batch id 再运行一次，记录是否返回 `idempotent=true`。保留真实退出码和输出，不得为了展示效果而删改失败。

## 10. 作为目标用户验收

实际检查：

- 是否清楚输出是候选而非裁决；
- 是否能看到 `human_review_required=true` 与 `authorized_action=false`；
- UNKNOWN 是否被保留并能解释；
- 反对证据、少数证据和来源引用是否仍可找到；
- 错误输入是否失败关闭；
- 相同输入重跑是否一致；
- 非开发者能否按文档完成操作；
- 哪些输出有帮助，哪些可能误导；
- 是否愿意在相同边界下继续使用。

最终选择 `ACCEPT`、`CONDITIONAL`、`REJECT` 或 `UNKNOWN`，并写明理由、限制、角色和日期。项目方期待不能成为改变结论的理由。

## 11. 删除与退出

逐项处理并记录：

- 原始材料；
- 脱敏工作副本；
- 临时文件与缓存；
- 运行账本；
- 导出和日志；
- 备份与同步副本；
- 按授权约定保留的最小审计记录。

删除后重新搜索工作区，记录验证方法与未解决例外。不得为了制造“全部删除”表象而破坏约定保留的不可变审计记录。

## 12. 最终回传包

只允许回传：

1. 授权清单引用或摘要，不含授权原文；
2. 脱敏报告，不含敏感正文；
3. 试点验收记录；
4. 删除与退出记录；
5. 候选、Pack、输入、信任库和允许日志的 SHA-256；
6. 已脱敏命令、退出码和 JSON 结果；
7. 失败、偏差、分歧与 UNKNOWN；
8. 角色重合和利益冲突披露。

严禁回传原始材料、可逆脱敏内容、私钥、密码、token、cookie、个人路径或未经批准的截图。回传前人工打开每个文件复核，并执行敏感信息扫描。

## 13. 项目方裁定边界

- Codex 可以检查文件齐备、摘要关系、版本冻结和状态字段；
- WorkBuddy 可以独立复核工程日志与证据一致性；
- 两者不能替参与者签署授权、人工脱敏或目标用户判断；
- 证据不足时保持 `COMMUNITY_EVIDENCE_PARTIAL`；
- 单个案例只支持该参与者、样本、版本和时间范围；
- 改变公开状态、Release 或部署仍需 Owner 单独授权。

## 14. 最简清单

```text
[ ] 我有权查看这批材料
[ ] 我披露了自己的全部角色
[ ] 授权范围和版本已经冻结
[ ] 脱敏检查有真实结论和剩余风险
[ ] 候选包身份与 SHA 匹配
[ ] 我实际运行了候选，而不是只看演示
[ ] 我保留了失败和 UNKNOWN
[ ] 我独立填写了 ACCEPT / CONDITIONAL / REJECT / UNKNOWN
[ ] 我完成并验证了删除/退出
[ ] 回传包不含原文、私钥和个人信息
```
