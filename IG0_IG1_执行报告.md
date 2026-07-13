# IG0 / IG1 执行报告

## 当前结果

```text
IG0 Baseline Verified:     PASS
IG1 Static Structure:      PASS
IG1 .NET Restore/Build:    NOT EXECUTED
Required .NET SDK:         10.0.301
```

## 已完成

- 校验 VG1、VG2、Implementation Plan、Schema、CASE005 和 Engine source identity。
- 建立 6 个源项目、5 个测试骨架项目和 `FullSpectrum.Observer.sln`。
- 固定 `global.json`、离线 `NuGet.Config`、构建规则和 lock 文件。
- 建立 Application ports、Execution、EngineFacade、Evidence 与唯一 CLI Host 边界。
- 建立 Python Worker 协议骨架，但没有调用或复制 Engine 正式算法。
- 建立 baseline/build/test/package PowerShell 脚本。
- 建立 Git 初始仓库和实施证据目录。

## 未完成

当前生成环境没有安装 `10.0.301`，所以没有把真实 `dotnet restore/build`
伪写成 PASS。请在 Windows 上运行：

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG1
```

三条命令通过后，IG1 才正式关闭，随后进入 IG2。
