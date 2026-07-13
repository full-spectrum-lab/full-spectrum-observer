# IG5 最小闭环候选执行报告

## 当前结果

```text
WP-04 Execution Pipeline Source:  COMPLETE CANDIDATE
WP-05 CLI Source:                 COMPLETE CANDIDATE
Python Reference Pipeline:        PASS
Source Architecture Check:        PASS
C# / .NET 10 Build:               NOT EXECUTED
Formal IG3:                       NOT PASSED
Formal IG4:                       NOT PASSED
Formal IG5:                       NOT PASSED
```

## 源码候选已经覆盖

- `BUILTIN_CASE` 与 `JSON_FILE` Intake；
- 文件路径、大小、JSON深度和SHA-256约束；
- `FoundationScenarioAdapter`；
- Schema Validation与Governance Validation分离；
- RuntimeConfigurationSnapshot、自包含digest和Schema检查；
- EngineFacade唯一正式调用；
- 单次请求1—300秒超时传递；
- Engine原始输出digest检查；
- Artifact、Observation和Audit原子Finalization；
- observer-only边界常量；
- `version / health / analyze / show / verify-audit` CLI；
- Idempotency重试和冲突处理；
- `CANCELLING → CANCELLED`取消状态路径；
- Engine依赖缺失时的结构化降级。

## 已执行证据

- 固定CASE005通过实际Python Worker和固定Engine执行；
- Engine结果与Golden一致；
- Engine输出digest一致；
- Python SQLite参考闭环完成Operation、Artifact、Observation和Audit关联；
- 静态边界检查通过，未发现HTTP、Console产品、Copilot、Connector或Registry；
- System源码中不存在`run_simulation`或第二套治理算法。

## 尚未完成的正式证据

当前环境没有冻结的.NET 10 SDK、win-x64 `sqlite3.dll`、私有Python 3.11 x64和Windows进程树环境，因此不能把以下内容写成PASS：

- C# restore/build；
- C# Unit/Contract/Integration测试；
- IG3 native SQLite正式执行；
- IG4 Windows Worker进程树与超时/取消；
- IG5真实CLI端到端执行。

## 下一步

在Windows正式环境依次执行：

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG2
pwsh ./scripts/test.ps1 -Gate IG3
pwsh ./scripts/test.ps1 -Gate IG4
pwsh ./scripts/test.ps1 -Gate IG5
```

只有前置Gate全部通过，才能正式关闭IG5并进入WP-06。
