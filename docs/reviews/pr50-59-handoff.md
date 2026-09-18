# Nori PR #50–#59 修复交接（未完成）

## 给接手助手的首条指令

继续本地集成分支 `local/pr50-59` 的完整审查修复，不是只做报告。先阅读本文件、当前源码、`AGENTS.md`，检查工作区；本轮修改已按问题提交在该分支，包含未完成的WIP，不能把WIP误认为验收通过。按下面问题顺序补完、验证并形成独立语义提交。**禁止读取、参考、合并或 cherry-pick PR #60。** 用户随后强调正在去 WebView 化：Main / Init / FirstRun 保持原生；只为尚未原生化的 Linux/macOS 音频、公开插件 HTML card 契约保留最窄兼容层；Windows native 不增设非 WAV 自动 WebView fallback。

本次会话因用户时间安排中止，并非验收完成。子代理均已停止。用户要求为了转移到 ChatGPT Work 将当前代码先提交 GitHub；因此已经按问题保存全部代码，包括明确标记的WIP，后续仍须完成实现和验证。

## GitHub 交接入口与进度提交

仓库：https://github.com/MF-Dust/Nori-Desktop-Pet

分支：`local/pr50-59`（不是main）。请直接检出该分支，所有进度代码已保存，不需要额外patch。

| 提交 | 内容 | 验证状态 |
|---|---|---|
| 111a24a | 非有限工具数字 | 对应18测试通过 |
| 16955c3 | Toast dispatcher/通知测试 | 待后端编译验证 |
| 75b40e9 | FirstRun导入/原生生命周期 | 待验证 |
| f4a0b42 | 播放owner/确定性测试 | 待验证 |
| f763743 | WASAPI COM释放同步 | 待验证 |
| ef4de48 | 麦克风状态机WIP | 半成品，缺确定性测试 |
| 32cb4a9 | TTS WAV请求与格式限制 | 待验证，MiniMax待处理 |
| 4ff1ed1 | 专用音频兼容宿主 | 前端测试通过，后端/真实平台待验证 |
| 75b93cd | 原生首页/隔离插件卡WIP | 半成品，缺安全与功能测试 |

接手优先：**先重新构建解决编译，再完成麦克风与插件卡测试/状态机，之后跑全套门禁。不要据commit的fix前缀认定已验证。**

## 分支及已提交结果

- 仓库：`C:/Users/SakuraStar/Desktop/Nori-Desktop-Pet`
- 当前分支：`local/pr50-59`
- `main` / `origin/main`：`cc96fe1d102876441d075e16116fe8937abf1e44`
- #59 head：`49b7f8c5`（已验证为当前 HEAD 祖先）
- 初始 HEAD：`2559368c457e4499825e8f96253b078b4f3cf634`，合并父节点为 main 和 #59 head，初始工作区干净。
- 首个已验证修复：`111a24a fix(tools): reject non-finite numeric arguments`；其余提交见下表，尚未完整验证。
- 未 fetch、merge、cherry-pick #60，也没有读取其实现。最终仍需核验完整 ancestry/diff。

### 已提交 #50

`BuiltinTools.OptionalNumber` 的 CLR/JsonElement double 及 string 解析都拒绝非有限值；long/int/decimal 保持原行为。`Describe` 可安全描述 CLR NaN/Infinity，避免 ToJsonString 自身失败。

`BuiltinToolArgumentTests` 新增 NaN/±Infinity/1e999 字符串、JSON 字符串、CLR double、JSON 数字溢出测试；正常整数/字符串20/字符串0.25原测试保留。必需参数报“不是数字”；可选 emotion/importance 沿用无效数值返回 null 后默认0.8的现有契约，NaN不进入业务层。

注意：CLR 非有限值在 ToolRegistry 的长度序列化阶段本来不能序列化；因此专门直接调用注册执行体验证公共解析器，未更改 ToolLimits。

## 已保存的待验证工作：接手必须重新读 diff、编译并验证

### #51 通知（代理完成实现，未通过整体编译验证）
- `Runtime/AppRuntime.Notifications.cs`：COM activation → `OnToastActivatedAsync`，Allow/Deny/Open 全部 Dispatcher.UIThread.InvokeAsync，退出检查、异步异常与日志失败兜底；只有 Open 显示窗口。
- `Nori.Desktop.Tests/ApprovalNotificationTests.cs`：真实后台线程激活、UI线程Open及授权收尾、不抢焦点、异常/无效参数测试。
- 跨平台快照失败根因：测试工程的 `OperatingSystem` 全局别名模拟 Windows，生产 toastSupported 其实已用真实OS；此测试改为 `System.OperatingSystem.IsWindows()`。
- 快照测试不再调用会移除真实COM/开始菜单注册的设置命令，只测试配置投影，避免系统副作用。
- 已保存为16955c3，仍需验证。

### #53 首次运行模型导入（代理被中止，已有实现和测试）
- 改动 `FirstRun/FirstRunSteps.cs`、`Windows/FirstRunWindow.cs`、`NativeFirstRunTests.cs`。
- 直接复用 Resources.Import 和原生文件选择器；未放宽 main bridge 来源授权。
- 需要检查成功刷新并选中、取消保留状态、失败可重试、空模型Next禁止、不能空model完成。
- 这些文件尚未完成主代理审查/测试，按实际落盘代码判断，不要凭代理摘要视作已完成。

### #54 / #11 原生窗口生命周期（已有未验证修改）
- `Windows/InitWindow.cs`、`NativeInitVisualTests.cs`、新增 `NativeWindowLifecycleTests.cs`。
- `WindowManager.Close` 和 Shutdown foreach 已补 InitWindow / FirstRunWindow / MainWindow.AllowClose。
- WindowManager生命周期与音频宿主改动已按hunk分别提交。
- 代理发现 `Runtime/AppRuntime.Init.cs:EnterMainFromInit` 仍 `Windows.Hide(Init)`，建议改为 Close；**尚未改**。用户原要求允许 Init Hide 但需停timer，是否必须Close需重新核实，不要盲改。
- 保留 Opened 下一帧初始化，不重新设计；验证无边框拖动、能力不支持时系统边框、Hide停timer、FirstRun完成真正关闭、Shutdown/updater restart不被拦截。

### #55 播放owner竞态（实现与测试已写，未验证）
- `Nori.Core/Voice/Audio/NativeAudioPlayback.cs`、`NativeAudioPlaybackTests.cs`。
- 使用现有CTS身份代表generation；只有owner可改全局playing/level；设备由播放任务释放，不在Dispose中与pump并发释放。
- 新增精确信号覆盖 A→B替换、创建竞争、Stop/Open、Dispose、初始化失败；移除原相关延迟猜调度。
- 必须审查 Stop/Dispose及时性与事件回调锁行为，再运行测试。

### #56 WASAPI（确认额外问题，已有修复，未验证）
- `Windows/WasapiAudioDevice.cs`、新增 `Nori.Desktop.Tests/WasapiAudioDeviceTests.cs`。
- 原来锁外 COM Write/Drain/Stop 可与 FinalRelease 并发；改用同一设备锁覆盖COM调用/生命周期；Stop先置位唤醒再取锁，Dispose幂等。
- tests 用托管COM替身和精确信号核对锁/volume。
- WasapiNativeApi ABI/前置声道映射已审查未修改；仍需验证 mono复制、8声道只前置L/R、设备拔出转AudioDeviceException、Stop打断Write/Drain、保持共享模式。

### #57 麦克风（明确半成品）
- `NativeMicrophoneRecorder.cs` 已改，代理正把 Idle→Starting 预留做成原子操作、后台任务独占设备释放时中止。
- **NativeMicrophoneRecorderTests.cs 尚未改**，原固定 Task.Delay 时序问题尚未解决。
- 接手重点：重读状态机；双Start仅一个成功；Starting/Recording交界Stop不泄漏；Open失败回Idle；Dispose不留pump/device/CTS；Stop打断阻塞Read；立即停止仍可产合法44字节空WAV。
- fake Read进入/样本完成必须用TCS/event，不能增加任意delay。

### #58 TTS格式（已有实现/测试/文档，未验证）
- `VoiceProviders.cs`：OpenAI response_format=wav；GPT-SoVITS POST/GET显式 media_type=wav 与 streaming_mode=false。
- `IndexTtsProvider.cs`：只补WAV响应契约说明，未乱加参数。代理用Context7查Modelverse官方 `api_doc/audio_api/ttts.md` 确认固定WAV。
- `NativeAudioFactory.cs`：非WAV错误提示可操作，要求服务返回PCM WAV；已有配置 audio_backend=webview 是用户显式重启兼容选择，不新增flag、不自动起Windows WebView。
- tests：`VoiceProviderTests.cs`、`IndexTtsProviderTests.cs`、新增 `NativeAudioFactoryTests.cs`、`AudioBackendTests.cs`（WebView保留原MIME）。
- `docs/跨平台.md` 新增TTS格式限制说明。
- **发现尚未修复：MiniMaxTtsProvider 仍硬编码 audio_setting.format=mp3，Windows native同样不兼容；建议核实API支持WAV并一并修复/测试，或明确说明限制。** Gemini已包装WAV。
- 必须验证OpenAI WAV实际工厂可解、非WAV单次失败后后续WAV仍能播放、不重复合成。

### #59 专用音频宿主（主代理已有实现，尚未编译通过）
- `WindowLabels.AudioHost = "audio-host"`；不加入 WindowDefinition.All。
- `WindowManager` 私有 `_audioHost`，CreateAll末尾按 `AudioBackend.PrefersNative(config,真实OS)` 决定创建，不入 `_windows` / All /可见性/导航。
- **CreateAll发生在 services.Runtime赋值前**，所以这里不能读取 services.Runtime.AudioBackendName（曾引发nullable编译错误，已改直接用共用选择器）。
- `GetNoriWindow(AudioHost)` 返回专用字段；Shutdown显式AllowClose/Close宿主。
- `NoriWindow.StartAudioHost`：ShowActivated=false、无taskbar、屏幕外1x1透明窗口Show以挂接native control；NavigationCompleted下一帧Hide，保留WebView音频页生命周期。
- **待验证风险：Linux/macOS真实NativeWebView Hide后WebAudio/MediaRecorder是否继续工作、权限和后台限速；未做实机验证，不能声称已可用。** 若无界面attach有更可靠现有方案可替换，但不可回退原生Main。
- AppRuntime音频channel改为解析 AudioHost，不再Main；native不调用窗口resolve。
- BridgeCommands六个audio回报只允许AudioHost；BridgeCommandRouter只允许该来源调用六个audio回报；NoriBridge拒绝其emit。
- index.html入口改为 src/bootstrap.ts；audio-host仅动态加载services/audio，不加载Vue/RUNTIME；其他入口仍main.ts。没有改Vite base。
- 新增 `DedicatedAudioHostTests.cs`，引用 `NativeWindowLifecycleTests.cs` 的私有partial测试lifetime，尚未编译/运行；测试真实manager建立宿主、native零依赖、bridge限制。
- 新增 frontend `tests/audio/bootstrap.test.ts` 已随前端153测试通过。

### #59 原生首页与插件（明确未完成）
- `Main/HomeView.cs` 已写 MCP统计/Steam/NoriOS/QQ复制/Bilibili/GitHub等原生入口。
- 新增 `Main/PluginWidgetHost.cs`、`Nori.PluginRuntime/PluginWidgetBridge.cs`。
- 方向：原生折叠卡片入口，展开才创建单张受限HTML WebView，折叠/隐藏/撤销插件时释放；Home默认不创建WebView。独立about:blank包装，不载主bridge，不走主index/router；宿主绑定plugin id、插件action路由、独立插件缓存。
- 代理正补安全/首页测试时中止，**尚未新增对应tests**；须审查整个隔离边界、导航、iframe sandbox、postMessage来源/action授权、隐藏时真正释放、加载错误不阻断Home。
- 代理发现旧Vue PluginWidgets.vue 的iframe同源无sandbox、信任页面自报pluginId，可访问主bridge/跨插件；原计划一起加固但 **Vue该文件目前未修改**。请核实并修复公开卡片入口的身份绑定/权限边界，补PluginWidgets tests。
- 最近一次后端编译失败为 PluginWidgetHost缺 `WindowsWebView2EnvironmentRequestedEventArgs` 命名空间；代理随后已加 `using Avalonia.Platform`，但**没有重新构建确认**。

## 已运行验证：不要把未执行项写成通过

环境：Windows，.NET SDK 10.0.400，pnpm 11.0.9。所有开发命令从app/desktop运行；只用pnpm。禁止并发dotnet共享bin/obj。

1. `dotnet test Nori.Core.Tests/Nori.Core.Tests.csproj --filter FullyQualifiedName~BuiltinToolArgumentTests --verbosity quiet`：最终 **18/18通过**。早期4失败是CLR非有限值在registry序列化前被阻断，后来改为直接执行体专测；已提交测试最终全绿。
2. 通知+宿主targeted Desktop测试尝试：**未进入测试，构建失败**。先WindowManager nullable（已修），后PluginWidgetHost缺using（代理称已修，待复验）。命令filter包含通知/ApprovalToastTests/专用音频/音频兼容宿主等中文方法名。
3. `pnpm install --frozen-lockfile`：通过，Already up to date。
4. `pnpm test`：**32 files /153 tests通过**，包含新bootstrap tests；当时后续代理尚在编辑C#，不代表最终整个仓库验证。
5. `pnpm check:todo`：通过。
6. `pnpm lint && pnpm build`：组合命令工具显示用户中止；但 `/tmp/nori-frontend-build.log` 实际已有完整 `✓ built in 18.58s` 产物日志，说明lint成功进入build且vue-tsc/vite构建完成。为严谨交接记作“日志显示成功，最终exit未由主代理确认”，请再跑一次。
7. `git diff --check`：中止后检查无输出。
8. **没有执行完整Core/Desktop tests、solution Release build/test、任何coverage、真实OS音频/截图/发布冒烟、最终独立review。Codex CLI存在，但本轮未运行Codex review。**

`components.d.ts` 在构建后git状态可能显示modified但numstat为空，是生成/换行噪声，不要恢复用户原先已丢弃的旧修改，也不要把这项作为功能修复提交。

## 后续验证及提交要求

先收尾半成品并解决编译；按用户原顺序逐项跑targeted并拆commit，不能为过测试删测试/放宽断言/屏蔽平台。保留现有架构，tabs/LF/中文注释，不新增TODO/FIXME，不做无关大重构。WindowManager相同生命周期special cases可小范围收敛但无须扩张。

完整门禁以 `.github/workflows/build.yml` 和 app/desktop/package.json 为准：

```sh
cd app/desktop
dotnet test Nori.Core.Tests/Nori.Core.Tests.csproj
dotnet test Nori.Desktop.Tests/Nori.Desktop.Tests.csproj
dotnet build Nori.slnx --configuration Release
dotnet test Nori.slnx --configuration Release --no-build --no-restore -m:1
pnpm install --frozen-lockfile
pnpm check:todo
pnpm lint
pnpm build # 包含 vue-tsc，无独立typecheck script
pnpm test
pnpm coverage
pnpm coverage:dotnet --no-build --no-restore
```

CI还包括Windows启动器测试、NORI_CAPTURE_SETTINGS=1 / NORI_CAPTURE_MEMORY=1 的视觉测试、FDD发布ZIP/SBOM/三种启动冒烟；涉及UI改变需720×480与1080p视觉证据。Linux/macOS无法本地运行必须明确未验证，纯Core测试不能依赖Windows；注意Desktop测试OperatingSystem别名陷阱。

建议后续提交按tools（已有）、notifications、first-run、playback、WASAPI如独立、microphone、WAV、audiohost、home/lifecycle拆分，不能一锅提交所有半成品。

最终报告必须：各问题/commit映射、测试改动、实际完整验证命令及结果、范围外问题、git status、git log --oneline origin/main..HEAD、#60排除证据。最终修复完成仍需工作区干净；此次交接提交仅保存进度，不代表功能验收完成。

