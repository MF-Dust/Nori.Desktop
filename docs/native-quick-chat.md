# 原生 Quick Chat

## 参考与实现边界

视觉与行为基线是 [Nori.Web master 03e4880a](https://github.com/MF-Dust/Nori.Web/tree/03e4880a92ad08d0ab5b24787596ed92fc82aec3)：`conversation-panel.css`、`conversation-panel.tsx`、`conversation-presentation.ts`。参考仓库未修改。

`PetWindow` 保留唯一的原生 Live2D；`QuickChatWindow` 是独立、透明、无边框、不显示任务栏的 Avalonia 窗口。它始终置顶，显示、流式事件和气泡更新不激活窗口。用户点击或 Nori 原生窗口内的 Ctrl+K / ⌘+K 才能聚焦输入。没有注册系统级全局热键。

`WindowManager` 管理 `QuickChatController`，后者监听桌宠位置、Bounds、DPI、屏幕工作区、模型与布局事件。输入框贴住模型视口下沿，气泡向上增长；联合布局约束到当前工作区。关闭快捷聊天不隐藏桌宠，隐藏桌宠不修改快捷聊天设置。

## 视觉对应

| Web 规则 | 原生对应 |
| --- | --- |
| 200–280 px composer，12 px 圆角，8/12 px padding | 200–280 DIP、46 DIP 高、12 DIP 圆角、8/12 DIP 内边距 |
| 深灰透明渐变、薄荷边框、多层阴影 | `QuickChatView` 色板、渐变和 4 层 composer 阴影 |
| focused 浅色/薄荷渐变，scale 1.02，200 ms ease | 独立模板与色彩/阴影/缩放过渡；移除主题附加的蓝色焦点环 |
| Nunito 400/500/600 | 内嵌静态字体资源；中文优先 Microsoft YaHei UI / PingFang SC / Noto Sans CJK SC |
| 14 px 输入、20 px 行高 | 14 DIP、20 DIP；聚焦同步切换文字和 caret |
| 独立 Ctrl/⌘、K 键帽 | 未聚焦且快捷输入可用时显示，聚焦后隐藏 |
| 28×28 发送按钮，16 px ArrowUp | 28×28 DIP、8 DIP 圆角、16 DIP 图标画布、薄荷渐变和发光 |
| 气泡最大 85%，间隔 8，底距 12 | 同样的宽度比例、间距、非对称圆角与发送方对齐 |
| 400 ms 进入、350 ms 退出 | 同样的位移、缩放、透明度及 cubic-bezier 曲线 |

背景使用仅限 composer 圆角区域的 Avalonia acrylic 和 Web 渐变，**不启用整窗模糊**。透明原生顶层窗口无法保证像浏览器一样采样其他应用的内容；没有可用 backdrop 时使用磨砂色回退。跨平台效果不是浏览器 backdrop-filter 的逐像素等价实现。

Reduce Motion 使用平台偏好；未知平台保守禁用动画。没有找到桌面版对应的 `comms-norichat-focus/send/receive` 资源或事件映射，因此没有引入或重复播放音效。

## 半身投影

`PetPresentation` 明确分离 Ordinary 与 QuickChat。QuickChat 为 `arg-nori` 和 `nori` 提供独立裁切预设，从实际 Drawable 范围计算投影缩放和平移，不修改模型、贴图、动作文件。默认窗口是 280×190 DIP，为完整耳饰保留顶部空间；脸部位于输入框左侧上方，贴近用户参考图的偏左构图，底部裁到颈肩/上胸。未知模型保留实际范围全宽并取上部区域，避免套用 Nori 的横向裁切。

快捷模式跟随用户缩放，但将该模式的视觉倍率限制在 0.75–1.5，输入框仍是 280 DIP；普通模式的配置倍率不改写，关闭后恢复。渲染、交互区域、点击映射和眼神追踪共享最终 `PetViewportProjection` / `PetViewportMapping`。Alpha 命中仍从最终渲染帧采样。

## 配置、来源与后端

`quick_chat_enabled` 默认 true；托盘使用稳定的“快捷聊天：开启/关闭”文本状态，原生常规设置提供相同开关，无托盘平台不丢失入口。二者复用 ConfigStore 与运行时快照通知，不维护第二份持久状态。

QuickChat 使用固定的 `NativeChatSurface.QuickChat` / `quick-chat` 身份及独立取消令牌，不冒充完整 ChatWindow。只允许 `chat_start`、`chat_cancel`、`chat_history_page`、`approval_respond`、`approval_extend`。来源类型、标签、可见性、生命周期与白名单在现有入口共同验证。

业务继续走 `NativeChatService` → `BridgeCommandRouter` → 现有 Agent；消息写入同一真实聊天历史，完整 ChatWindow 保留 Markdown、语音、清空等能力并同步历史版本。审批卡展示工具、描述、参数详情、允许/拒绝与超时，回复始终来自原始 QuickChat source；打开完整对话不转移审批权限。

首批历史只登记，不弹出旧消息。浮动时间线最多 3 条，每条从首次收到起存活 30 秒，流式 chunk 不延长寿命。输入最多 100 个 Unicode 码点；IME 预编辑 Enter 不发送，重复提交被阻止，失败不覆盖新草稿。关闭时先拒绝审批、取消会话，再释放来源。

## 平台与验证

Windows/macOS 根据鼠标所在交互区域切换透明空白穿透，X11 使用 Input Shape；能力不足的平台使用紧凑窗口边界。PetWindow 原有 Windows 命中测试、分层样式、X11 与 macOS 路径不替换。

截图位于 [screenshots/native-quick-chat](screenshots/native-quick-chat)。20 张命名场景覆盖 720×480、1920×1080；另外 40 张覆盖中英文及 200/280 DIP 内容宽度。真实 Win32/OpenGL 的 `quick-chat-pet.png` 与 `quick-chat-composer.png` 来自隔离安全模式，不依赖用户数据库、不发送网络对话。两张原生窗口截图分别捕获；黑色区域是窗口抓图的透明背景表现，并非生产窗口黑色背景。

截图测试必须独立进程运行，避免普通 Headless 测试污染字体/渲染服务。工作目录 `app/desktop`，命令遵循 RTK wrapper：

```powershell
rtk proxy dotnet build Nori.slnx --configuration Release
rtk proxy dotnet test Nori.Desktop.Tests --configuration Release --no-build --no-restore -m:1
rtk proxy dotnet test Nori.Core.Tests --configuration Release --no-build --no-restore -m:1
rtk proxy pnpm check:todo
```

Skia 截图：设置 `NORI_CAPTURE_CHAT=1`，运行 `--filter FullyQualifiedName~NativeQuickChatVisualCapture`。默认输出到 `app/desktop/artifacts/native-quick-chat`；设置 `NORI_SETTINGS_CAPTURE_DIR` 可指定其同级目录。

真实 Windows Live2D：设置 `NORI_CAPTURE_QUICKCHAT_NATIVE=1`、`NORI_QUICKCHAT_MODEL_DIR`（本地合法 arg-nori 模型目录）、`NORI_QUICKCHAT_NATIVE_CAPTURE_DIR`，单独运行 `--filter FullyQualifiedName~QuickChat真实窗口`。测试只复制模型至临时 fixture，关闭并清理自己的窗口。

真实平台仍需人工验证：Windows 中文输入法候选确认、拖拽跨不同 DPI 显示器、与其他应用的焦点/置顶交互；macOS 与 X11/Wayland 的透明/穿透/工作区变化。当前桌面自动化连接不可用，不能将截图测试表述为这些交互已人工验收。

## 本地验证结果

2026-09-19，Windows / .NET 10，Release：

- `dotnet build Nori.slnx --configuration Release`：0 警告、0 错误。
- `Nori.Core.Tests`：1202 通过，0 失败。
- `Nori.Desktop.Tests`：675 通过，0 失败；13 项按既有约定跳过，需要模型或显式启用截图。Quick Chat 的截图另外在独立进程执行。
- 独立 Skia 截图：2 项通过，输出 60 张；独立 Win32/OpenGL 实机截图：1 项通过，输出桌宠和输入窗口 2 张。布局测试覆盖 100% / 125% / 150% DPI，但没有把它们表述为三档真实显示器截图验收。
- 最后修复聚焦缩放/阴影裁切后：103 项 Quick Chat / Native Chat 相关回归通过，截图 3 项重新通过；新增祖先裁切边界断言。
- `pnpm build`：Vue/TypeScript 检查与 Vite 生产构建通过；本次只同步了通用设置快照的类型契约，没有改动 Vue 页面。
- `pnpm check:todo` 与 `git diff --check`：通过。

可直接查看 [默认输入栏](screenshots/native-quick-chat/quick-chat-empty-720x480.png)、[聚焦状态](screenshots/native-quick-chat/quick-chat-focused-720x480.png)、[三条气泡](screenshots/native-quick-chat/quick-chat-three-bubbles-720x480.png)、[审批卡](screenshots/native-quick-chat/quick-chat-approval-720x480.png) 和 [真实 Live2D 裁切](screenshots/native-quick-chat/quick-chat-pet.png)。
