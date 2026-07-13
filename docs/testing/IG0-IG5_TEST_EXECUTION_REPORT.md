# Observer v0.1 Foundation Kernel IG0–IG5 测试执行报告

报告状态：PASS / IMPLEMENTATION CANDIDATE  
被测提交：Gitee `f2fba54`  
执行日期：2026-07-13  
执行环境：Windows 10 x64，本地断网目标架构

## 1. 结论

IG0–IG5 在正式仓库工作区全部通过。结果证明 Foundation Kernel 源码能够锁定构建，合同与 Schema 一致，Evidence Core、Engine Bridge 和最小执行闭环可运行。该结果不代表 IG6–IG8、离线安装包、第二环境复现或 v0.1.0-alpha 发布已经完成。

## 2. 环境

| 项目 | 值 |
|---|---|
| OS | Windows 10.0.19045 x64 |
| .NET SDK | 10.0.301 |
| Python | 3.11.9 x64 embedded/private runtime |
| NumPy | 1.26.4 |
| jsonschema | 4.25.1 |
| SQLite | 3.50.4 official win-x64 DLL |
| Engine | v1.0.0 / `09062bae…` |
| CASE | CASE005_KNOWLEDGE_CONFLICT |

## 3. 总结果

| Gate | 结果 | 摘要 |
|---|---|---|
| IG0 | PASS | 51/51 冻结文件摘要与大小一致 |
| IG1 | PASS | locked restore；11 个 .NET 项目构建；0 warning / 0 error |
| IG2 | PASS | 5 unit、5 contract、9 architecture checks；12/12 Schema；50 reason codes；canonical vectors |
| IG3 | PASS | 4 C# native SQLite tests；6 Python SQL oracle checks |
| IG4 | PASS | 9 Worker checks；1 C# Facade integration check |
| IG5 | PASS | 1 C# minimum-loop test；15 Python reference checks |

## 4. 缺陷与修复回归

| 缺陷 | 首次结果 | 修复 | 回归 |
|---|---|---|---|
| PowerShell 5.1 不支持 `utf8NoBOM` | IG0 FAIL | 使用 UTF8Encoding(false) | IG0 PASS |
| baseline lock 中文路径乱码 | 25/51 | 按冻结 SHA-256 恢复 26 路径，并显式 UTF-8 读取 | 51/51 PASS |
| NuGet lock 与项目图不一致 | NU1004 | force-evaluate 后恢复 locked mode | locked restore PASS |
| .NET 10 API/继承变化 | build FAIL | 使用 IOException、byte[] JSON parse | build PASS |
| 分析器与 wire 命名冲突 | 56 errors | 保留 wire 常量，说明性抑制非功能规则 | 0 warning / 0 error |
| Facade 参数与时间文化相关 | build FAIL | 对齐接口名、InvariantCulture | build PASS |
| PowerShell 5.1 Path API 缺失 | IG2 FAIL | 明确 Windows 绝对路径正则 | IG2 PASS |
| Python 环境缺 jsonschema | IG2 FAIL | 固定 jsonschema 与 typing_extensions | IG2 PASS |
| Python 自带 SQLite DLL 原生崩溃 | IG3 0xC0000005 | 换用官方 SQLite 3.50.4 | IG3 PASS |
| evidence 静态写“未执行” | 证据矛盾 | Gate context 动态标记 formal status | IG3–IG5 `PASSED` |

## 5. 关键测试明细

### IG2

- canonical object ordering、nested vectors；
- 12 个 Schema meta/instance 验证；
- 50 个 reason code 唯一性与命名空间；
- 项目引用方向、无治理算法复制、Facade 单一 Worker 启动点；
- Snapshot 禁止浮动引用；
- Worker request/response 版本与 digest 合同。

### IG3

- Audit append 与链验证；
- Audit mutation 拒绝；
- idempotency 首次/重试/冲突；
- Observation/Artifact/Audit 原子完成；
- Snapshot/Audit 不可变 SQL 语义；
- Artifact digest 验证。

### IG4

- stdout 单行 JSON；
- Worker 成功退出；
- Engine golden 相等；
- 输出 digest；
- 重复执行确定性；
- 错误 commit 与错误协议拒绝；
- stderr 不污染 stdout；
- C# Facade 实际启动私有 Python 3.11。

### IG5

- 完整十阶段链路；
- operation completed；
- Observation 指向 Artifact 和 Audit head；
- GENESIS/previous hash/event hash/payload digest；
- Observer 边界四字段；
- Engine output golden 与 digest。

## 6. 已知限制

- IG6 尚未覆盖完整的注入、路径逃逸、SQLite 损坏/锁争用、并发 hash chain、超时孤儿进程、网络外联和 canary 聚合测试。
- IG7 尚无可安装/可解压运行的最终离线客户端包。
- IG8 尚未由非实现者在第二环境复现。
- 当前客户端是 CLI/Foundation Kernel，不是最终 Web Console。
- 只验证 synthetic CASE，不代表企业价值或生产可用。

## 7. 判定

```text
IG0–IG5: PASS
Implementation Candidate: YES
IG6 Entry: GO
v0.1.0-alpha Release: NO
```

