# Native Sentry 遥测与发布配置

Nori 仅在原生 .NET/Avalonia 宿主中集成 Sentry，不上传页面 source map。音频错误写在宿主本地结构化日志里。

原生 Sentry 由用户遥测设置控制。没有配置 DSN 时，SDK 不会初始化远程传输；关闭遥测后停止发送事件，本地诊断日志仍保留。

## 数据边界

原生遥测只记录经过清理的异常、固定操作名、release/environment 标签和性能指标。严禁发送聊天正文、提示词、记忆、语音转写或录音、MCP 参数与结果、API Key、Cookie、请求正文及用户身份。Native Sentry 与本地日志不承担模型下载、更新服务或用户数据备份。

原生界面与音频等模块通过宿主共用日志器记录固定安全事件，不接受通过桥接提交任意日志正文或来源。真实异常继续由现有异常处理和遥测链路捕获；本地诊断页不制造测试崩溃或合成日志，也不会上传本地日志文件。

## 应用会话跟踪（Release Health）

配置 Native DSN 且用户明确同意遥测后，SDK 自动开始一次应用会话。重复启用不会另开会话；关闭遥测或正常退出时先结束会话，再由 SDK 在现有关闭超时内刷新。重新开启遥测会开始新会话。隐藏窗口或驻留托盘不会结束会话。

会话记录用于统计 release/environment 下的会话数量、时长、错误与无崩溃率，不是聊天会话跟踪或屏幕录制。已处理异常计入错误；非终止的未处理异常保留 `Unhandled` 状态；终止异常标记为 `Crashed`，退出清理不会覆盖为正常结束。仅覆盖现有宿主异常链路可捕获的崩溃，强制杀进程或断电不保证上报。

会话使用 SDK 的匿名安装标识，不含聊天内容；未配置 DSN 或未同意遥测时不启动会话。正式发布应注入下述 release 配置，以便在 Sentry Release Health 中按版本查看。

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

Release 顺序保持为：版本与分发许可校验、主题令牌检查、Native 三平台构建/测试和发布冒烟、Native 符号上传、创建 tag、完成 Native Sentry release、创建 GitHub Release。

本机可按需设置 `NORI_SENTRY_DSN_NATIVE`、`NORI_SENTRY_RELEASE` 和 `NORI_SENTRY_ENVIRONMENT`。不要把 `SENTRY_AUTH_TOKEN` 保存进项目文件或发布包。
