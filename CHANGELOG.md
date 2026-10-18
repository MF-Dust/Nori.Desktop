# 更新记录

## [Unreleased]

### Added

- 思考档位：AI 设置新增「思考强度」(自动/关闭/最低/低/中/高/超高/最大)。按协议换算为 `reasoning_effort`、Anthropic `thinking.budget_tokens`、Gemini 3 `thinkingLevel` 或 Gemini 2.5 `thinkingBudget`，并按模型声明的档位就近取值；「自动」不发送任何参数。Anthropic 开启思考后工具往返原样回传签名思考块，DeepSeek 等 interleaved 模型回传 `reasoning_content`，思考文本不进入回复正文。
- 关于页贡献者：作者栏改为从 GitHub 仓库 `MF-Dust/Nori.Desktop` 获取贡献者并只显示头像 (悬停显示登录名)，机器人账号不展示。列表与头像缓存于 `data/resources/cache/github-contributors.json`，24 小时刷新，过期时先显示旧缓存再后台更新；安全模式只读缓存。新增桥接命令 `about_list_contributors`。
- 模型能力目录：从 [models.dev](https://models.dev) 获取上下文窗口、输出上限、思考档位与多模态能力，精简缓存于 `data/resources/cache/models-dev.json`，24 小时刷新；安全模式只读缓存。设置页显示当前模型信息。
- 上下文自动压缩：上下文上限按模型窗口计算 (「自动」最多 128K，可选完整窗口或固定值)；历史接近上限时用当前模型把较早对话合并为摘要 (`chat_context_summary`)，此后只读取摘要之后的消息。压缩失败时退回确定性截断；清空聊天记录会一并清除摘要。
- 图像生成：新增 `generateImage` 工具 (confirm 级别)，支持 OpenAI 兼容 Images API、Gemini 图像模型与 Imagen；图片保存在 `data/resources/generated/images`，以 `nori-image:` 引用附在回复末尾，聊天窗口原生显示并可点击用系统程序打开；朗读与快捷对话气泡会剥离图片引用。地址/密钥留空时沿用同协议的对话服务。
- 插件 AI 提供商 (`ai.provider`)：插件可贡献返回 `Microsoft.Extensions.AI.IChatClient` 的聊天提供商，以 `plugin:<pluginId>:<id>` 出现在 AI 设置中；对话、模型列表、连接测试、桌面视觉、伴侣互动、回复反应与记忆反思共用同一注册表。插件撤销后不回退到 OpenAI，而是明确报不可用并取消在途请求。
- 插件设置内嵌页：`IPluginPageContribution.Placement = Settings` 的页面直接出现在设置侧栏「插件设置」分组，停用或撤销时同步卸下并释放。
- 插件应用互操作 (`applications`)：查询进程、按绝对路径启动可执行文件并等待退出；不提供终止进程，Windows 上拒绝 `.bat`/`.cmd`。
- 插件管理：新增 `plugin_reload` 重新加载已启用插件 (可切换到已安装的新版本，旧程序集无法回收时返回待重启)；设置页支持搜索、仅显示运行中、刷新，并可展开查看、启停及手动测试插件动作 (仅 safe)。

### Changed

- 视觉风格对齐 Nori.Web 的 theme-nori：设计令牌改用深海玻璃底色、冰青发丝线与 Tailwind 圆角刻度；控件模板由 Devolutions macOS 主题换为 Avalonia Fluent 并以 Nori 令牌覆盖 (6px 圆角、深色输入井、品牌青主按钮与开关)；窗口外壳改为 36px 透明标题栏、居中标题、web 交通灯与激活时的渐变描边；设置页去掉卡片，改为标题加发丝线分组；对话气泡改为 web 的薄荷/深灰渐变。内嵌 Plus Jakarta Sans (界面) 与 Nunito (对话) 拉丁子集字体，中文继续回退 Noto Sans SC。
- 模型协议改由 LlmTornado 统一实现：移除 OpenAI、Anthropic、Google.GenAI 官方 SDK 及 Microsoft.Extensions.AI.OpenAI，上层仍使用 Microsoft.Extensions.AI 抽象；Embedding 改为直接实现 `/embeddings` 协议。win-x64 发布目录减少约 12 MiB（压缩包约 3.5 MiB）。模型请求仍经共享 HttpClient 转发，保留公网地址校验、响应上限与 Nori User-Agent；错误保留 HTTP 状态码与服务端说明，对已配置密钥脱敏，避免附带客户端库拼接的请求头与请求正文。
- 浏览器 DOM 自动化拆分为独立插件 [Nori.Plugins.Browser](https://github.com/MF-Dust/Nori.Plugins.Browser)（`io.nori.browser`）：主程序移除 Microsoft.Playwright 依赖、`automation_browser_*` 桥接命令、`automation_browser_enabled` 配置与设置页浏览器控件；历史浏览器审计记录仍可读取。
- 宿主 Plugin API 升至 2.3：`IPluginActionContribution.Permission` 可声明 safe/confirm/dangerous，插件动作工具按级别逐次请求授权；对应 `Nori.PluginSDK` 2.3.0 已同步页面与权限合同并移除 WebView 合同。
- 宿主 Plugin API 升至 2.4：插件动作注册时校验 ID、描述与参数 Schema，组合工具名超长的动作单独跳过；对应 `Nori.PluginSDK` 2.4.0 新增合同，2.4.1 补充校验规则文档。

### Fixed

- 修复公网工具请求在发送前被全局系统代理探测误拦截的问题：默认公网客户端明确禁用代理，连接时解析、校验并连接同一组 IP，保留 TLS 校验与手动重定向策略。
- 保留显式 `allow_public_system_proxy` 配置与模型客户端行为；显式代理模式不保证代理端 DNS 解析的私网隔离。
- 涉及 `NoriHttpClients`、`UrlAccessPolicy`、工具调用与启动配置，新增代理路由和 DNS 策略回归测试。
- 仓库更名为 `MF-Dust/Nori.Desktop` 后，更新检查、手动下载链接、更新清单脚本的默认仓库与主页 GitHub 链接仍指向旧名称 `MF-Dust/Nori-Desktop-Pet`，请求无法通过授权仓库校验，更新检查会失败；现统一改用新名称，Issue / PR 模板与文档链接同步更新。
- 补登 Fusion Pixel 字体 (SIL OFL 1.1) 的第三方组件声明：该字体随窗口扫描分析芯片内嵌分发，此前未写入 `third-party-components.json`，发布元数据中缺少这一条目。
