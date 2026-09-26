# Native Sentry 遥测与发布配置

Nori 仅在原生 .NET/Avalonia 宿主中集成 Sentry。隐藏音频 WebView 不加载 Sentry SDK，也不上传 source map；音频宿主的错误通过受限 Bridge 写入本地结构化日志。

原生 Sentry 由用户遥测设置控制。没有配置 DSN 时，SDK 不会初始化远程传输；关闭遥测后停止发送事件，本地诊断日志仍保留。

## 数据边界

原生遥测只记录经过清理的异常、固定操作名、release/environment 标签和性能指标。严禁发送聊天正文、提示词、记忆、语音转写或录音、MCP 参数与结果、API Key、Cookie、请求正文及用户身份。Native Sentry 与本地日志不承担模型下载、更新服务或用户数据备份。

音频 WebView 的日志使用 `write_log` Bridge 命令。宿主忽略传入正文和伪造来源字段，只接受稳定事件名与已知错误类型；调用侧对同类错误每分钟限流。

## 原生构建变量

MSBuild 从环境变量或 `Nori.Desktop.csproj` 属性读取以下原生配置。值通过构建中间文件进入程序，不写入仓库：

```text
NORI_SENTRY_DSN_NATIVE
NORI_SENTRY_RELEASE
NORI_SENTRY_ENVIRONMENT
```

Release workflow 将 Sentry release 固定为 `nori@<数字版本>`，不包含 codename 或提交 hash。程序版本、Sentry release 和发布 tag 的职责不同：codename 用于产品版本、tag 和产物命名，informational version 可包含当前短提交 hash。

## GitHub Actions Secrets

Native Sentry 集成使用：

- `SENTRY_AUTH_TOKEN`：构建期符号上传和 release 管理凭据，不得进入用户包。
- `SENTRY_ORG`：Sentry 组织名。
- `SENTRY_PROJECT_NATIVE`：Native Sentry 项目名。
- `SENTRY_DSN_NATIVE`：注入 Native 程序的公开项目标识，不是鉴权令牌。
- `SENTRY_URL`：自托管 Sentry 地址；SaaS 默认使用 `https://sentry.io`，可省略。

Web Sentry 的 DSN、项目名和 source-map 上传配置已移除。

## Native 符号与 release

Release workflow 在发布归档前保留 PDB，使用 Sentry CLI 上传 Native 符号，然后从 Windows、Linux 和 macOS 发布目录移除 PDB。用户 ZIP/tar.gz 不包含符号文件。Native release 在正式 tag 创建后由独立 job 创建或复用并 finalize；缺少 Sentry 凭据时跳过相关上传与 finalize，不阻断普通应用发布。

Release 顺序保持为：版本与分发许可校验、前端音频宿主构建及相关检查、Native 三平台构建/测试和发布冒烟、Native 符号上传、创建 tag、完成 Native Sentry release、创建 GitHub Release。

本机可按需设置 `NORI_SENTRY_DSN_NATIVE`、`NORI_SENTRY_RELEASE` 和 `NORI_SENTRY_ENVIRONMENT`。不要把 `SENTRY_AUTH_TOKEN` 保存进项目文件或发布包。
