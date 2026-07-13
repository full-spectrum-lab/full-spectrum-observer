# IG2 Contracts Executable 候选执行报告

## 正式状态

```text
Branch: feat/IMP-0101-ig2-contracts-candidate
IG0 Baseline Verified:       PASS
IG1 Static Structure:        PASS
IG1 .NET Build:              NOT EXECUTED
IG2 Source Candidate:        COMPLETE
IG2 Reference Validation:    PASS
IG2 Architecture Static:     PASS
IG2 Source Static Review:    PASS
IG2 .NET Build / C# Tests:   NOT EXECUTED
IG2 Formal Gate:             BLOCKED BY IG1 BUILD
```

## 本轮源代码实现

- 12 个 Foundation Kernel 顶层合同 DTO；
- 集中式 `System.Text.Json` 严格配置；
- 50 个 reason_code 常量与目录加载、域前缀和重复检查；
- `FS-OBS-CANON-1`、SHA-256 和自包含 digest 排除字段规则；
- 12 个 Schema、12 个实例、目录文件的 SHA-256 锁定；
- JSON Schema Draft 2020-12 Foundation 子集验证器；
- Common Fragment 一致性检查；
- Runtime Snapshot 精确版本与非零 digest 策略；
- 无第三方 NuGet 的 Unit / Contract Console Test Runner；
- 架构依赖与 VAC-FK-001 静态检查；
- Python reference oracle，仅作验证证据，不进入产品运行时。

## 已完成的参考验证

- Schema 元验证与实例验证：12/12 PASS；
- reason_code：50 个、无重复、域前缀一致；
- 架构依赖方向：PASS；
- System 中不存在正式治理算法副本：PASS；
- C# 源文件结构检查：PASS；
- Canonical reference vectors：PASS。

## 尚未关闭

当前环境没有可运行的精确 .NET SDK `10.0.301`，因此不能声称：

- `dotnet restore` 已通过；
- `dotnet build` 已通过；
- C# Unit / Contract Runner 已执行；
- IG1 或 IG2 已正式关闭。

## Windows 关闭步骤

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG2
```

三步全部通过后，才允许将本分支合并到 `main` 并宣布 IG2 PASS。
