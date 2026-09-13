# 原生对话迁移与验收记录

## 已迁移范围

对话入口改为复用独立 Avalonia `ChatWindow`，正文 `ChatView` 可复用，不创建 WebView。默认 960×640 DIP，最小 720×480 DIP，仅深色，不跟随系统浅色。普通关闭先停止录音再隐藏，保留草稿、滚动位置和正在生成的会话；应用退出才取消会话并释放订阅。

设置、模型、记忆已经是原生窗口。本次不迁移主页、首次运行、初始化页；`main` WebView 继续常驻承载 TTS/STT、媒体令牌交换及口型同步。`PluginWidgets` 移至主页，`OperationDrawer` 的自动化审批仍在主窗口，与原生对话工具审批分开。主动提醒仍沿用原有桌宠动作与自动朗读路径，本次不增加主动提醒气泡。

旧 `ChatView.vue`、`ChatMessageBubble.vue`、Vue chatStore、聊天 Markdown/split 辅助及其专属测试已删除；无剩余调用的 `markdown-it`、`dompurify`、`.chat-markdown` 样式与 `glass-panel` shortcut 同步移除。没有移除主窗口音频代码、插件挂件或共享国际化词条。

## 外观与交互对照

| 项目 | 原生实现 |
| --- | --- |
| 深色框架 | 背景 `#171B22`；间距采用原 rem × 10 的 DIP 尺寸 |
| 助手气泡 | 浅青 `#b8d7d8` / 深色文字 `#111827`，左对齐；圆角 14/14/14/4 |
| 用户气泡 | `#454752` / 白字，右对齐；圆角 14/14/4/14；消息行限制约 84% 宽 |
| 头部 | 标题、模型、词元/速度、缓存、技能/工具数量、清空；窄窗口自然换行 |
| 空态与输入 | 四条建议；输入区 40–120 DIP，麦克风、发送/停止；Enter 发送、Shift+Enter 换行，检查 IME 预编辑 |
| 消息 | 历史分页、流式预览、最终文本替换、失败保留草稿与重试、复制、回到最新/未读提示 |
| 授权 | 深色页内弹层、队列、参数展开、倒计时、拒绝/允许/延期；关闭与 Escape 均不隐式批准 |
| Markdown | Markdig 1.3.2 解析为原生控件；标题、列表、引用、代码、表格、外链；结构化消息与流式不拆泡 |

保持原有配色与布局层次，不声明浏览器到 Avalonia 的逐像素一致。原生字体抗锯齿、窗口装饰、长链接/行内代码换行与浏览器不同；裸域名（如 `example.com`）不自动转链接，显式 HTTP/HTTPS 链接可用。

## 接口与安全约束

- `window_open_chat` 仅允许可见主 WebView 调用；两个入口打开同一缓存窗口，不向 WebView 路由表新增 `chat`。
- 原生 `INativeChatSource` 使用真实来源引用及固定命令白名单，桥接路由与命令入口双重校验。会话事件定向送回原始来源，不订阅可由 WebView 伪造的通用广播。
- 新增快照 `chat: { configured, backend: "local" | "luolicore" }`，识别远端配置，不用本地 `ai.configured` 误挡 LuoLiCore。
- `approval_extend({requestId}) → {deadlineUtc}`，请求和延期事件带真实截止时间。初始最多 60 秒，延期不能越过上游工具期限。提交时在授权锁内复核取消信号和可见性；隐藏仍可拒绝，不能批准、延期或发起新请求。
- `chat_clear → {remoteReset, note}`；清空与生成/最终落库共享引擎闸门，远端重置失败保留本地记录。安全模式只处理本地并提示远端未联系。终结事件前释放闸门，自动朗读不占用生成闸门。
- UI 缓冲 `chat_start` 返回前的事件，忽略旧会话及无会话 ID 的朗读状态对活动轮次的影响；取消后等待终结事件解锁。不增加两分钟静默取消，以兼容远端长等待。
- 历史先过滤旧工具反馈再分页，界面按 ID 去重及代次失效；首批历史加载成功前不发送，失败提供重试。
- 录音继续走 main 音频宿主，转写追加到草稿，不自动发送；关闭前等待启动/停止请求，失败保留重试机会。退出前先预检录音停止，避免其他原生页面先被不可逆释放。
- Markdown 不执行 HTML，不加载图片或内嵌媒体；仅绝对 HTTP/HTTPS 地址可经宿主打开。参数在原生文本控件中显示，不经过 HTML/XAML 执行。

## 自动验证结果

命令均从 `app/desktop` 执行；本仓库解决方案实际位于该目录的 `Nori.slnx`。

| 验证 | 结果 |
| --- | --- |
| `pnpm check:todo` / `pnpm lint` / `pnpm build` | 通过 |
| `pnpm test` | 30 文件、136 项通过 |
| `pnpm coverage` | 通过；行覆盖率 56.83%，报告 `coverage/` |
| `dotnet build Nori.slnx -c Release` | 通过，0 警告、0 错误 |
| `dotnet test Nori.slnx -c Release --no-build --no-restore -m:1` | 1344 项通过，7 项按环境开关跳过 |
| 原生聊天专项（不启用截图） | 73 项通过；截图另起进程执行 |
| `pnpm coverage:dotnet --no-build --no-restore` | 收集通过，产物 `artifacts/dotnet-coverage/` |
| C# LSP | 原生聊天、窗口、运行时及审批竞态测试检查无诊断 |

新增 `NativeChatBridgeTests`、`NativeChatRuntimeTests`、`NativeChatStateTests`、`NativeChatWindowTests`、`NativeChatMarkdownTests`、`NativeChatVisualTests`。覆盖来源伪装/越权、隐藏审批、锁等待期间隐藏与取消、真实延期/超时、清空与生成互斥、终结后 TTS 不占闸门、远端就绪、事件先到、最终正文替换、历史代次/去重、输入同步、语音失败重试、草稿与控件保留、实时语言和深色交互态。前端测试覆盖原生入口、主窗口音频生命周期、主页插件挂件。

全量测试的 7 个跳过为 3 个需要真实模型/原生环境的预载测试与 4 个按开关启用的截图测试；不是忽略失败。聊天截图已单独执行通过。

## 截图证据

```bash
NORI_CAPTURE_CHAT=1 dotnet test Nori.Desktop.Tests -c Release --no-build --no-restore -m:1 --filter FullyQualifiedName~NativeChatVisualCapture
```

输出：`app/desktop/artifacts/native-chat/manifest.json` 与 36 张 PNG。覆盖中英文 × 720×480 / 960×640 / 1920×1080 × 空态、历史、Markdown、流式、错误、授权。使用真实 Avalonia/Skia headless 渲染和合成对话数据；检查主题、有效像素、横向溢出及输入区裁切。已抽查浅青气泡、代码/表格、窄窗授权和英文空态。

截图必须使用上述独立测试进程；本机将默认 headless 功能测试与 Skia 截图混跑时曾出现字体/渲染器缓存污染（缺字或空帧）。独立运行后 36 张通过。这些截图不是实机窗口截图，也没有与旧 Vue 做自动像素差比较。

## 仍需实机人工验收

以下没有在本轮自动化中声称通过：

- Windows/macOS/Linux 实际窗口、125%/150% DPI、多显示器缩放和系统窗口装饰。
- 中文输入法候选确认、连续组合输入、焦点切换后的 Enter/Shift+Enter。
- 真麦克风权限拒绝/断连，隐藏时停止录音，TTS 实际播放及 Live2D 口型同步。
- 真实提供方及 LuoLiCore 的长时流式、重连/错误提示、实际高风险工具审批。
- 对照旧版截图进行人工视觉微调；现有截图只能证明当前原生布局与颜色可检查，不等于跨平台视觉验收完成。
