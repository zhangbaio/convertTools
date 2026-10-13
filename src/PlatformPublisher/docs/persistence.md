# 平台助手数据库持久化

## 数据库边界

全局主库：`%LocalAppData%\YunfanPlatformPublisher\app.db`。

项目库：`<workflow>\.yunfan-platform.db`。

主库保存设置、加密凭据、平台账号、发布任务/步骤、逐素材事件和统计数据。项目库保存项目状态文档、快手上传断点和ADX批次镜像。视频、封面、PDF、浏览器Profile、截图和大日志仍保存在文件系统。

## 安全与并发

- SQLite启用WAL、外键和30秒busy timeout。
- 每个数据库使用进程内写入门；任务和步骤在同一事务保存。
- ADX密码和登录态以Windows DPAPI当前用户密文BLOB保存到`secure_settings`。
- 运行中任务在异常重启后恢复为待执行。
- 成功的逐素材结果通过稳定事件ID幂等更新。

## 旧数据导入

首次读取时自动导入：

- `publish-accounts.json`
- `publish-jobs.json`
- `weixin-workflow-settings.json`
- `adx/settings.json`、加密密码及登录态
- `platform-settings.db`
- `analytics.db`
- 快手和ADX项目侧车状态

旧数据库导入记录源路径和SHA-256；相同文件不会重复导入。旧文件不自动删除。

## 备份和诊断

顶部“数据库”入口可执行完整性检查、查看大小和创建SQLite一致性在线备份。备份目录为：

`%LocalAppData%\YunfanPlatformPublisher\migration-backups`。

不要在应用运行时直接复制`app.db`，否则可能遗漏WAL中的未检查点数据。
