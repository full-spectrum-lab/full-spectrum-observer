# Observer v0.1.0-alpha IG8 独立验收结果

日期：2026-07-13  
被测旧包 SHA-256：`6afdf1a54b385661154d04c22aa216c44bff2f88fa0360dd5d966c0cbdb576d3`  
结论：**NOT_READY；旧包不得发布，修复后必须重新执行 IG8**

## 已通过的功能证据

- 第二台 Windows 22H2、无系统 .NET、未下载依赖；
- ZIP 摘要匹配，解压结构完整；
- version、health、CASE005 analyze、show、verify-audit 全部成功；
- 两次运行产生不同 Observation，确定性 Engine 输出一致，历史未覆盖；
- Audit Chain 从 1 条增长为 2 条且完整；
- 无遗留进程、无 Observer 外联连接、无个人绝对路径泄露；
- SQLite 触发器阻止 Audit 与 Runtime Snapshot 的 UPDATE/DELETE。

## 阻断与复核结论

1. `LICENSE` 明确写着许可证尚待项目作者决定，而旧 SBOM 错误声明 Observer 为 Apache-2.0：High 阻断成立。
2. 旧 SBOM 漏列 attrs、jsonschema、jsonschema-specifications、referencing、rpds-py、typing-extensions，并漏列 .NET/Engine 许可证：Medium 成立。
3. 报告所称 ReleaseManifest `path` 为空属于字段读取错误：合同字段实际名为 `relative_path`，4 个关键文件均有非空路径。但 Manifest 只列关键文件、全文件仅在 SHA256SUMS 中，工程侧仍决定强化为 Manifest 全 payload 清单。
4. `manifest_sha256` 是按合同对“不含自身字段的规范 JSON”计算，不等于整个文件摘要；原报告对此为推测，工程侧验证逻辑正确。

## 已实施整改

- SBOM 不再在作者未决定时虚假声明 Apache-2.0，改为显式 `PENDING_OWNER_DECISION`；
- 自动扫描全部 `.dist-info`，补齐捆绑 Python 组件和许可证表达式；
- 补充 .NET MIT 与 Engine `MulanPSL-2.0 OR Apache-2.0`；
- ReleaseManifest 改为列出所有 payload 文件，Schema 强制 `relative_path` 与 `classification`；
- 验包逐项重算 Manifest 文件摘要，并在许可证未决定时强制 FAIL。

## 唯一作者级阻断

项目作者必须依照 QPP《AI协作授权边界》明确选择 Observer 许可证。AI 不得代替作者作该法律与治理决定。决定落库后才能生成 IG7-R2 正式候选，并由独立执行者对新 SHA-256 包重新执行 IG8；旧包结果不得继承。
