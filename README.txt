双击 启动Observer.cmd 启动；浏览器将自动打开本地控制台；仅监听本机 127.0.0.1，不对外暴露。

说明：
- 启动Observer.cmd 等效于运行 observer.cmd serve，是官方推荐的一键入口。
- 控制台仅在本地回环地址（127.0.0.1）监听，不会对外暴露网络端口。
- 从公开 Release 下载时，请将 observer.zip 与 observer_IDENTITY.json 放在同一目录，
  再把 ZIP 解压到该目录下的子文件夹。双击入口会自动读取同级公开身份文件，
  以便“系统信息”页面显示公开 ZIP 的 SHA-256；只有 ZIP 时会诚实显示不可用。
- 需要指定独立数据目录时，可在解压目录的同级位置创建 observer-launch-settings.json，
  内容仅允许 {"data_directory":"C:\\绝对路径"}。命令行 --data-dir 和环境变量
  Observer__DataDirectory 的优先级更高；未提供任何配置时仍使用稳定的用户数据目录。
- runtime/RUNTIME-INVENTORY.md 记录包内 .NET、Python、NumPy、OpenBLAS 与 SQLite
  的版本、用途、摘要及许可证线索，用于解释便携式发布包的体积和运行时来源。
- 这是 v0.3 维护候选（beta.2 之后），非生产就绪版本（PRODUCTION_READY = NO）。
- 停止：关闭启动它的命令行窗口即可优雅退出控制台。
