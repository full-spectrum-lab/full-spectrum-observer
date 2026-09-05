# Engine v1.5 隔离验证分支

**分支：** `compat/engine-v1.5-observer-v0.4`  
**目的：** 在不改变主分支 v1.0.0 worker lock 和既有证据链的前提下，验证 Observer v0.4.0-beta 与 Engine v1.5 runtime 的组合行为。

## 当前基线

主分支已发布证据锁定：

```ini
ENGINE_VERSION = v1.0.0
ENGINE_COMMIT = 09062bae2c7608bda79ee4bfde5779109e8e6197
OBSERVER_RELEASE = v0.4.0-beta
```

## 隔离验证要求

在本分支实际运行前，必须同时更新并核对：

1. `engine/worker.lock.json` 的 Engine 版本、commit 和 vendored 文件哈希；
2. Worker request 的版本和 commit；
3. Engine v1.5 对应的 CASE005 golden；
4. IG4/IG5/IG6 证据输出；
5. 独立证据包及其 SHA-256。

## 当前状态

```ini
ENGINE_V1.5_ADAPTER_TEST = PASS
ENGINE_V1.5_FIXTURE_TEST = PASS
ENGINE_V1.5_RUNTIME_API = PASS
ENGINE_V1.5_OBSERVER_IG4_IG6 = NOT_EXECUTED
MAIN_BRANCH_V1.0_EVIDENCE = PRESERVED
PRODUCTION_READY = NO
```

**边界：** 本分支的创建不构成 v1.5 runtime 兼容性通过。只有完成锁定依赖替换、完整门禁和独立证据包后，才能更新 Protocol 兼容矩阵。
