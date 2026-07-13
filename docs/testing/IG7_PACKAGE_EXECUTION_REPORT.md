# Observer v0.1.0-alpha IG7 离线制品测试报告

状态：PASS  
日期：2026-07-13  
实现提交：`2b35970`

## 结论

IG7 离线客户端制品 Gate 正式通过。打包脚本生成了包含私有 .NET Runtime、Python 3.11、SQLite、固定 Engine、Schema、案例与文档的 Windows x64 ZIP，并在全新解压目录完成整包校验和实际运行。

## 制品

- 文件：`artifacts/ig7/full-spectrum-observer-v0.1.0-alpha-ig7.zip`
- SHA-256：`6afdf1a54b385661154d04c22aa216c44bff2f88fa0360dd5d966c0cbdb576d3`
- 已核验文件：1960
- 机器证据：`evidence/ig7/IG7_Result.json`

## 已通过项目

- ReleaseManifest JSON Schema 与规范摘要校验；
- `SHA256SUMS.txt` 全文件完整性校验；
- CycloneDX SBOM 随包生成；
- 解压后 `observer.cmd version --json`；
- 解压后 CASE005 Knowledge Conflict 离线分析；
- Audit Hash Chain 校验；
- 对制品文件实施篡改后能够拒绝通过；
- 运行期间无需安装 pip/NuGet 包，也无需联网。

## 版本边界

本制品固定 Engine `v1.0.0`，不支持 Engine v1.5。Engine v1.0/v1.5 Compatibility Adapter 规划在 Observer `v0.2.0-alpha`，不能将本次 IG7 结果描述成已与 Engine v1.5 打通。

## 剩余发布门

IG8 仍需由非实现者在第二个干净 Windows 环境独立完成解压、运行、审计、停止与清理复现。在 IG8 通过前，本制品是验收候选包，不是正式 Release。
