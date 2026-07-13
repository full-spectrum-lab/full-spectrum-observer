# Observer v0.1.0-alpha IG8-R2 独立复验结果

日期：2026-07-13  
结论：**PASS**

## 复验对象

- 版本：`0.1.0-alpha`
- Engine：`v1.0.0` / `09062bae…`
- 许可证：`MulanPSL-2.0 OR Apache-2.0`
- ZIP SHA-256：最终值以同名 `.zip.sha256`、Release Wiki 和 tag 附件为准

## 结果

- 完全解压到独立 clean directory；
- 包内 Python 验包器校验全部 payload、Manifest、SBOM 和许可证：PASS；
- version、health、analyze、show、verify-audit：退出码 0；
- 两次 analyze 的 Engine output SHA-256 一致；
- `observer_only=true`，无 certified/authorized/active_external 提升；
- Audit chain 验证通过；
- 命令退出后无 Observer/Python Worker 残留进程。

## 边界

该结果只适用于报告所绑定的最终 release commit 和 ZIP SHA-256。本版仍是 Windows x64 CLI Foundation Kernel，不含 Console、Engine v1.5 Compatibility、企业 Connector 或生产 SLA。
