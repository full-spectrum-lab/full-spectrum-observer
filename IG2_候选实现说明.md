# IG2 Contracts Executable 候选实现说明

当前分支：`feat/IMP-0101-ig2-contracts-candidate`

已实现源代码：
- 12 个顶层合同 DTO；
- 集中 JSON 策略；
- 50 个 reason_code 常量和目录加载；
- `FS-OBS-CANON-1` 与自包含 digest；
- Schema Bundle 完整性校验；
- Foundation Schema 子集验证器；
- 无第三方 NuGet 的 Unit/Contract Console Test Runner；
- 架构静态验证和 Python reference oracle。

正式状态：
- IG0：PASS
- IG1 静态结构：PASS
- IG1 `.NET build`：NOT EXECUTED
- IG2 reference validation：待本轮执行
- IG2 C# build/tests：NOT EXECUTED
- IG2 Gate：BLOCKED BY IG1 BUILD

因此本分支不得合并到 `main`，直到 Windows 精确 SDK 构建和 IG2 C# tests 通过。
