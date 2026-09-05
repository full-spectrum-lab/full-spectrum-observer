# Engine v1.5 runtime smoke（隔离探索）

**日期：** 2026-09-05  
**分支：** `compat/engine-v1.5-observer-v0.4`

在隔离分支中临时将 Worker 的身份校验切换为 `v1.5.0 / LOCAL_V15_CHECKOUT`，并将 `--engine-root` 指向本地 `full-spectrum-engine-github-v15`，运行 CASE005 请求。

结果：

```ini
WORKER_RESPONSE = SUCCESS
ENGINE_VERSION = v1.5.0
OUTPUT_SHA256 = 51a74d6ff458f3d48f82c7e04ed7c279826b79f789140ff96e364122edf9bf80
EXPECTED_OUTPUT_MATCH = PASS
```

这证明本地 v1.5 Engine 的 Worker 调用形状和 CASE005 输出与既有 golden 一致。该 smoke 不是正式 IG4/IG5/IG6：没有更新 vendored lock、文件哈希、Release 证据包或正式 Engine commit，因此不能升级兼容矩阵为正式 `PASS`。临时 Worker 修改已恢复，主分支和隔离分支的正式锁定链未被改变。
