# Observer v0.1 Foundation Kernel IG6 测试执行报告

状态：PASS  
代码提交：`0da50e5`  
日期：2026-07-13

## 结论

IG6 自动化 Gate 正式通过。统一入口完成 IG0–IG5 回归、C# 运行时故障注入、Python 安全聚合、21 项冻结 VG5 测试需求映射，并生成 `evidence/ig6/IG6_Result.json`。

```text
status: PASS
formal_gate: PASSED
```

## 执行入口

```powershell
./scripts/test.ps1 -Gate IG6 \
  -PrivatePython <absolute-python-3.11-path> \
  -SqliteNativeDirectory <absolute-sqlite-directory> \
  -GenerateEvidence
```

## 主要覆盖

- Snapshot digest 篡改阻断；
- Audit UPDATE/DELETE、防篡改定位、并发 sequence/hash chain；
- Worker 超时、进程终止、超大输出、多行协议与 Worker hash；
- 输入路径逃逸、大小限制、敏感 canary 不出现在错误和数据目录；
- 第二写者锁、损坏 SQLite 阻断；
- Adapter 失败后恢复；
- runtime/Engine/Worker digest；
- shell/command 注入边界；
- Raw Input 不持久化；
- Artifact 临时文件清理；
- Worker 进程内网络 deny guard；
- IG0–IG5 正式证据聚合。

## 发现并修复的缺陷

并发测试发现：多个分析同时产生相同 Engine 输出时，content-addressed Artifact Store 会竞争同一目标文件。修复方案为按内容 digest 建立进程内异步锁；同摘要写入成为幂等操作，之后 6 路并发 finalization 的 Audit sequence 连续且整链校验通过。

## 证据

- `evidence/ig6/IG6_Result.json`
- `tests/Observer.Tests.Integration/Program.cs`
- `scripts/ig6-harness.py`
- `engine/worker/offline_guard.py`

## 边界

IG6 证明本地 Windows 测试主机上的自动化安全和可靠性控制。IG7 仍需证明离线制品完整，IG8 仍需由非实现者在第二干净环境验证安装、运行、审计、停止与清理。
