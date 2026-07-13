# IG3 / IG4 并行候选执行报告

## 已完成

- WP-02 Evidence Core C# source candidate
- SQLite migration、native C API adapter、Artifact Store、Operation、Idempotency、Snapshot、Observation、GLOBAL Audit Hash Chain
- WP-03 Engine Bridge C# source candidate
- 固定 Engine 源码依赖、Worker lock、单行 JSON 协议、超时/取消/进程树终止、输出 digest 校验
- Python SQLite reference oracle：PASS
- 实际 Engine Worker CASE005 golden smoke：PASS
- IG3/IG4 source integration static verification：PASS

## 仍未完成

- .NET SDK 10.0.301 restore/build
- C# Unit/Contract/Integration runner execution
- win-x64 pinned sqlite3.dll runtime artifact
- private Python 3.11 x64 bundle
- Windows Job/Process Tree high-risk test

## 正式 Gate 状态

```text
IG3 Evidence Core Ready:  NOT PASSED
IG4 Engine Bridge Ready:  NOT PASSED
WP-04 Execution Pipeline: NOT STARTED
```

本分支只证明两条并行支线的源代码和参考 Oracle 可以合并，不代表可以跳过 IG3/IG4 直接进入 WP-04。

## 本轮源代码复核加固

- 取消令牌在 Worker 进程启动前检查，避免已取消请求仍创建子进程。
- Worker stdin 写入失败时终止进程并观察 stdout/stderr 读取任务。
- Schema 目录要求绝对路径。
- Evidence 清理与回滚路径只吞掉预期的 I/O/存储异常，不使用无类型 catch。
- 50 个 C# 文件完成词法复核：PASS（不等同于编译）。

## 当前正式边界

当前分支是 `integration/IG3-IG4-source-candidate`。Python 参考验证与实际
Engine Worker smoke 均为 PASS；但 .NET 10 编译、C# 集成测试、固定 Windows
`sqlite3.dll`、私有 Python 3.11 和 Windows 进程树测试尚未执行，因此 IG3/IG4
仍为 NOT PASSED，WP-04 仍为 NOT STARTED。
