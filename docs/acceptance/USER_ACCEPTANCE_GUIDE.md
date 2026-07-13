# Full Spectrum Observer v0.1.0-alpha IG7 用户验收指南

## 重要版本边界

本候选包固定 **Full Spectrum Engine v1.0.0**，没有与 Engine v1.5 打通。Engine v1.0/v1.5 Compatibility Adapter 属于 Observer v0.2.0-alpha。

本包是 Foundation Kernel CLI 候选，不是最终 Web Console，也不代表企业生产可用。

## 验收前

1. 使用 `SHA256SUMS.txt` 和 `ReleaseManifest.json` 验证包完整性。
2. 将 ZIP 解压到不含受限权限的本地目录。
3. 断开网络也应可以完成下面所有步骤。

## 手工验收

在解压目录打开命令行：

```bat
observer.cmd version --json
observer.cmd health --data-dir acceptance-data --json
observer.cmd analyze --case CASE005_KNOWLEDGE_CONFLICT --data-dir acceptance-data --json
observer.cmd show --observation-id <analyze返回的observation_id> --data-dir acceptance-data --json
observer.cmd verify-audit --from 1 --data-dir acceptance-data --json
```

验收关注：

- version 显示 Observer 0.1.0-alpha、Engine v1.0.0 和固定 commit；
- analyze 返回成功、Observation、Snapshot、Output 与 Audit 引用；
- `observer_only=true`；`certified/authorized/active_external=false`；
- verify-audit 返回完整链通过；
- 第二次运行仍可成功，历史记录未被覆盖；
- 断网状态下无下载提示或外部连接要求；
- 错误信息不包含完整敏感输入。

## 验收反馈

请记录操作系统、解压目录、每条命令退出码、截图/输出、是否易懂、失败步骤和期望改进。你的验收属于产品作者验收；IG8 还需要另一位不了解内部实现的执行者做第二环境复现。
