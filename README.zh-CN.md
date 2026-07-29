# Full Spectrum Observer

[![全频谱三层入口与三大核心组件](https://github.com/full-spectrum-lab/full-spectrum-commons/blob/main/diagrams/architecture/three-entry-three-core-components-zh-v10.png?raw=1)](https://github.com/full-spectrum-lab/full-spectrum-commons/blob/main/docs/three-entry-three-core-components.md)

**Observer 在体系中的位置：** Protocol 定义主体和治理契约，Engine 提供确定性分析，Observer 将获得授权的本地事实连接到 Evidence、Replay 与人工决策点。当前公开版本只做观察，不执行企业或生产系统的最终动作。

[English](README.md) · [简体中文](README.zh-CN.md)

> 面向可复现 Evidence、审计追踪和有限人工复核的本地优先 Observer 应用。

## 版本真相

| 版本线 | 状态 | 范围 |
|---|---|---|
| [`v0.2.0-alpha.2`](https://github.com/full-spectrum-lab/full-spectrum-observer/releases/tag/v0.2.0-alpha.2) | **公开预发布** | Foundation Kernel 上的 Engine v1.0/v1.5 兼容适配层 |
| `v0.3.0-beta` | **正在研发，尚未发布** | 本地单用户 Operator Console |
| `v0.4`～`v1.0` | **已设计，尚未实现** | Scenario Pack、企业节点、多主体 Service 与真实组织验证 |

当前 GitHub Release 是源码发布。Wiki 路线或规划文档不等于已经交付的可安装客户端。

## 当前已实现能力

- 固定 `.NET 10`、`win-x64` 构建基线；
- 不可变 Runtime Snapshot 与原生 SQLite Evidence Core；
- 通过 `Observer.EngineFacade` 隔离调用私有 Python 3.11 Engine Worker；
- 在 Engine v1.0/v1.5 兼容层中保留版本、Profile、UNKNOWN、reason code、Replay 和 Audit 语义；
- CASE005 确定性 fixture、Evidence manifest 与离线 Gate；
- `MulanPSL-2.0 OR Apache-2.0` 双许可证。

历史候选分支报告继续保留作审计，但不覆盖本页版本状态表。

## 架构边界

```text
Observer 应用（.NET 10）
  → Application / Evidence Core
  → Observer.EngineFacade
  → 固定私有 Python Worker
  → 固定 Engine 合同
```

只有 `Observer.EngineFacade` 可以启动 Engine Worker。Observer 不重新实现 FSHI、Risk、ESS、Gate、UNKNOWN、Explanation 或 Runestone 计算，也不认证、授权或执行企业最终业务动作。

## 源码复验

```powershell
pwsh ./scripts/verify-baseline.ps1
pwsh ./scripts/build.ps1 -Configuration Release -Locked
pwsh ./scripts/test.ps1 -Gate IG1
```

IG3/IG4 等门禁需显式提供固定的私有 Python 3.11 和原生 SQLite 路径，脚本不会自动联网下载依赖。

## 入口

- [Releases](https://github.com/full-spectrum-lab/full-spectrum-observer/releases)
- [CI](https://github.com/full-spectrum-lab/full-spectrum-observer/actions/workflows/foundation-gates.yml)
- [Evidence](evidence/)
- [冻结基线](docs/baselines/)
- [源码包 Manifest](SOURCE_PACKAGE_MANIFEST.json)
- [安全策略](SECURITY.md)
- [贡献说明](CONTRIBUTING.md)

## 许可证

使用者可以在木兰宽松许可证第 2 版与 Apache License 2.0 中任选其一。第三方组件继续适用各自许可证；正式发布包必须附准确 SBOM 和第三方声明。
