# Full Spectrum Observer v0.1.0-alpha

首个 Foundation Kernel 工程发布：提供完全离线的 Windows x64 CLI、固定 Engine v1.0.0 Facade、确定性 CASE005、SQLite/Artifact Evidence、不可变 Audit、SBOM、ReleaseManifest、SHA256SUMS 与独立验收指南。

## 许可证

Observer 自有工作采用 `MulanPSL-2.0 OR Apache-2.0` 双许可；接收者任选其一。第三方运行时和依赖保留各自许可证并记录在 SBOM。

## Gate

- IG0：51/51；IG1：locked build PASS；
- IG2 Contracts/Schema、IG3 Evidence、IG4 Engine Facade、IG5 minimum loop：PASS；
- IG6：31/31；
- IG7-R2：全部 payload、Manifest、SBOM、许可证和摘要 PASS；
- IG8-R2：clean extracted package command flow、确定性、Audit 和进程清理 PASS。

## 非声明

本版不是完整 Observer Console，不兼容 Engine v1.5，不是企业生产版，也不执行最终业务动作。后续 v0.2.0-alpha 才建设 v1.0/v1.5 Compatibility Adapter。
