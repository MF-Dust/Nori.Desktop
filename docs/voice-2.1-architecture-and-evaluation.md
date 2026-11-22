# Nori.Desktop 2.1 实时语音系统架构、本地离线引擎评估与实机验收清单

本文档记录 Nori.Desktop 2.1 实时语音系统的工程架构、能力模型、安全边界、H1 本地离线语音引擎评估结论、H3 后续联动边界以及三平台实机回归验收清单。所有架构描述以当前仓库实现为准，明确区分已由自动化测试覆盖项与需人工实机验证项。

---

## 1. Nori.Desktop 2.1 实时语音系统架构

### 1.1 六大职责边界

实时语音链路在 `Nori.Core` 与 `Nori.Desktop` 之间划分为六个互不越界的模块：

| 边界 | 核心类型 / 文件 | 职责与隔离约束 |
| --- | --- | --- |
| 1. 音频采集与连续重采样 | `IAudioCaptureDevice`、`CaptureOwnership`、`ContinuousAudioProcessor`、`StatefulLinearResampler`、`PcmLevel`、`WasapiCaptureDevice` / `CoreAudioCaptureDevice` / `AlsaCaptureDevice` | 按次打开显式选择的输入设备（留空使用系统默认设备），进程内由 `CaptureOwnership` 互斥占用；对任意设备块大小做残余帧对齐、多声道下混与跨块无漂移线性重采样，输出标准化 `0..1` 输入电平。 |
| 2. VAD 与有界音频缓冲 | `EnergyVoiceActivity`、`BoundedAudioQueue` | 本地能量语音活动检测（灵敏度 `0..1` 与静音收尾阈值夹紧），维护前置环形缓冲与有界待发送队列；溢出时立即释放缓冲，避免内存无限增长。 |
| 3. STT 提供商与能力注册表 | `ISttProvider`、`SttProviderCapabilities`、`SttProviderRegistry`、`OpenAiRealtimeSttProvider`、`GptTranscribeProvider`、`WhisperSttProvider`、`ILocalSttEngine`、`LocalOfflineSttProvider` | 统一声明流式、整段、语言提示、本地离线与硬件加速能力。`AppRuntime` 通过注册表与 `RealtimeSttPipeline` 按模式和能力选择服务。普通模式注册云端流式、云端整段及本地离线接缝；本地接缝不内置可用引擎。 |
| 4. 会话编排与统一轮次状态机 | `RealtimeVoiceSession`、`RealtimeVoicePhase`、`RealtimeVoiceTurn`、`RealtimeSttPipeline`、`RealtimeTranscriptionCoordinator`、`BoundedSttEventChannel` | 维护单调递增 `Generation` 与不可逆轮次交付状态机；处理流式会话创建、事件背压消费、提交前失败自动回退整段转写，以及旧代号陈旧结果丢弃。 |
| 5. TTS 合成与原生播放 | `VoiceService`、`ITtsProvider`、`IAudioPlayback`、`NativeAudioPlayback`、`SentenceChunker`、`AudioSynthesisCache` | 按句切段并行预取（有界流水线）、LRU 缓存音频并经平台原生声卡输出 PCM WAV；播放期间实时采样音量驱动 Live2D 口型，并与 `RealtimeVoiceSession` 联动暂停监听。 |
| 6. 原生 UI、桥接与安全诊断 | `ChatView.LiveVoice`、`NativeChatState`、`VoiceSettingsPage`、`BridgeCommands.RealtimeVoice`、`RealtimeVoiceSettings`、`SttSanitizer`、`DiagnosticExporter` | 原生聊天栏通过独立 80ms 轻量定时器读取内存态快照（不进入通用快照与日志），提供失败转写草稿保护；诊断导出与日志严格屏蔽录音、转写正文、凭据与设备原文。 |

### 1.2 统一轮次模型与状态机

`RealtimeVoiceSession` 以 `RealtimeVoicePhase` 与单调递增的 `Generation` 保证同一时刻只有一条有效语音轮次：

```text
Idle ──▶ Listening ──▶ Transcribing ──▶ Thinking ──▶ Speaking
 ▲           │               │              │            │
 │           └───────────────┴──────────────┴────────────┴──▶ Interrupting ──▶ Idle / Listening
 │
 └────── Error（失败后释放麦克风并允许用户显式重试）
```

- **代号隔离（`Generation`）**：开始新轮次、提交识别、主动打断或停止时递增 `Generation`；任何后台识别结果、临时字幕或 Agent 回复在进入下一阶段前都必须核对 `Generation`，旧代号结果直接丢弃，绝不会重复触发工具执行或覆盖新轮次。
- **幂等交付（`RealtimeVoiceTurn`）**：通过原子 `Interlocked.CompareExchange` 控制 `Pending -> PublishedBySession -> SubmittedToAgent`（或 `Pending -> Delivered`）状态迁移，确保一轮语音文本最多只交付并提交给 Agent 一次；取消令牌生效后拒绝再迁移。
- **模式与回退策略（`RealtimeSttPipeline`）**：
  - `SttMode.RealtimeCloud`：优先建立流式 WebSocket 会话；若在 `Commit` 之前发生传输/建连/队列溢出失败，允许按配置自动回退到同代号的整段文件转写，并在状态中标记 `UsedBatchFallback` 与 `FallbackReason`。
  - `SttMode.BatchCloud`：本地 VAD 截断完整一句话后，上传经 `AudioMime.ValidateRecorded` 校验的 WAV 录音至 `/audio/transcriptions`（默认 `gpt-transcribe`，兼容端点不支持时自动降级 `whisper-1`）。
  - `SttMode.Local`：在 `RealtimeSttPipeline` 与 `SttProviderRegistry` 中已强制约束仅匹配本地离线提供商、严禁降级到任何云端或联网 STT 服务；当前已注册 `LocalOfflineSttProvider`，但未挂载本地引擎，因此普通模式下尝试以 `local` 启动监听会提示当前无可用离线识别服务。

### 1.3 OpenAI Realtime GA WebSocket 协议

`OpenAiRealtimeSttProvider` 与 `OpenAiRealtimeSttSession` 实现 OpenAI Realtime GA 转写协议：

1. **地址与传输安全（`OpenAiRealtimeSttUriBuilder`）**：
   - 规范化端点为 `wss://.../v1/realtime?intent=transcription`（仅允许 `wss://` 或本地环回 `ws://127.0.0.1` / `localhost` / `[::1]`，拒绝非环回明文、URL `UserInfo` 携密及除 `intent=transcription` 外的查询参数）。
   - 鉴权仅通过 `Authorization: Bearer <apiKey>` 请求头传递，严禁将密钥拼接进 URL。
2. **会话初始化与参数**：
   - 连接建立后发送 `session.update`（`session.type = "transcription"`、默认模型 `gpt-live-transcribe`、音频格式 `audio/pcm` 24kHz 单声道、`input_audio_transcription` 可选 `language`），等待服务端返回 `session.updated` 后才进入就绪状态。
3. **音频发送与提交**：
   - 使用 `input_audio_buffer.append` 推送 Base64 编码的 24kHz 单声道 PCM16 小端字节；说话结束后发送 `input_audio_buffer.commit` 触发最终转写。
4. **事件归一化与有界背压**：
   - 单条服务端 WebSocket 消息上限 `1 MiB`（`MaxIncomingMessageBytes`），避免超大帧耗尽内存。
   - 事件经 `BoundedSttEventChannel`（默认容量 256，背压超时 5s）发布 `SessionUpdated` / `InputCommitted` / `Delta` / `Completed` / `Failed` / `Error` / `Closed`，单调递增 `Sequence`；`RealtimeTranscriptionCoordinator` 按 `ItemId` 关联增量字幕与终态，并将单轮字幕限制在 `8192` 字符内（截断时保护 UTF-16 代理对不被拆半）。

### 1.4 连续 PCM 管线与 VAD

- **任意块大小与声道适配**：`ContinuousAudioProcessor` 通过残余帧缓存对齐非整帧回调，并将多声道平均下混为单声道，避免跨设备回调丢帧或相位撕裂。
- **跨块连续重采样**：`StatefulLinearResampler` 在音频块之间保留历史末样本与分数相位（`0 <= Phase < 1`），支持将 16 kHz / 24 kHz / 44.1 kHz / 48 kHz 等设备采样率连续重采样为流式所需的 24 kHz 或整段所需的 16 kHz。
- **电平计算与 VAD**：`PcmLevel` 计算平滑后的标准化电平（`0.0 ~ 1.0`），在空闲/暂停/停止时立刻归零；`EnergyVoiceActivity` 结合用户配置的 `Sensitivity`（`0..1`）和 `SilenceMilliseconds` 判断起止点，并保留语音起始前导缓冲。

### 1.5 原生 UI 交互与草稿保护

- **轻量采样**：`ChatView.LiveVoice.cs` 仅在完整对话窗口可见时以 80ms `DispatcherTimer` 轮询 `AppRuntime.ReadChatVoice()`，不把高频电平或临时字幕写入通用快照、磁盘数据库或 `ChatMessage` 持久消息流。
- **草稿保护（`NativeChatState`）**：语音轮次经 `BeginSend(turn.Text, fromVoice: true)` 发送给 Agent；若发送或网络请求失败，`RememberFailedVoiceInput` 将已识别的转写文本暂存在内存中，`ChatView.LiveVoice` 展示「将转写追加到草稿」按钮（`RestoreFailedVoiceInput`），防止用户辛苦说完的一段语音因发送异常而丢失。
- **设置页联动**：`VoiceSettingsPage` 提供音量、TTS/STT 提供商、实时语音总开关、连续对话、VAD 灵敏度、静音阈值、主动打断开关与 10 秒受控麦克风测试。

### 1.6 全链路取消与 AEC 护栏

- **硬件互斥**：麦克风测试、按键说话与实时语音共用进程级 `CaptureOwnership` 原子锁，同一时间最多只有一个采集端打开设备。
- **无 AEC 护栏**：由于当前原生后端不内置系统级回声消除（AEC），在 `VoiceService` 进入朗读（`Speaking`）阶段或外部播放占用时，实时语音自动置为 `CapturePaused` 暂停麦克风检测，防止 TTS 外放声音回灌麦克风引发自激循环；同时 `InterruptEnabled == true` 时保留按钮/命令主动打断能力。
- **全链路取消**：设备读取（`device.Stop()` 在后台线程停止以防止堵塞 UI 线程）、WebSocket 收发（`CloseAsync` 带发送锁与握手超时）、进程执行与 TTS 播放均绑定联动 `CancellationTokenSource`。

### 1.7 安全隐私与指标体系

- **默认关闭与 `--safe-mode` 现状边界**：
  - `realtime_voice_enabled` 默认为 `false`；即使设为 `true` 也不会在启动时自动打开麦克风。`--safe-mode` 下注册表只注册本地离线接缝，实时语音按 `AllowNetwork = false` 筛选，且桥接与会话入口均拒绝启动实时语音。安全模式仍可在设置窗口纯本地枚举设备、测量麦克风电平；不会发起外部网络语音连接或远端模型下载。
- **零正文与零凭据泄露**：`SttSanitizer` 规范化并检查 API Key 不含控制字符，对所有错误文本执行密钥替换与 `SensitiveDataRedactor` 脱敏、单行化及 300 字符截断；日志与 `DiagnosticExporter` 不包含原始音频字节、识别正文、提示词或真实设备路径。
- **指标采样**：`AppRuntime` 与诊断导出共享 `VoiceMetricsTracker`。实时会话记录开口检测到首个真实增量、提交到最终流式转写、待发送音频队列峰值，以及监听恢复时设备重开失败次数；语音 Agent 轮次记录首个助手文本增量延迟；TTS 记录首段交给播放器的延迟。诊断 ZIP 仅导出数值汇总，不包含文本、音频、设备标识或凭据。

### 1.8 三平台设备管理

| 平台 | 音频后端 | 系统前提与注意事项 |
| --- | --- | --- |
| Windows | `WasapiAudioDevice` / `WasapiCaptureDevice` | 直接连接默认或指定 WASAPI 端点；蓝牙耳机 HFP（通话麦克风）与 A2DP（立体声播放）切换时设备采样率可能变化，依赖 `ContinuousAudioProcessor` 适配。 |
| macOS | `CoreAudioOutputDevice` / `CoreAudioCaptureDevice`（AudioQueue） | 打包 `Info.plist` 必须包含 `NSMicrophoneUsageDescription` 权限描述，否则系统直接拒绝采集；UI 线程不得同步阻塞音频停机。 |
| Linux | `AlsaAudioDevice` / `AlsaCaptureDevice`（`default` 设备） | 需安装 `libasound2t64`（Ubuntu 24.04+）或 `libasound2`，通过默认或指定设备接入 ALSA / PipeWire-ALSA；读取、挂起恢复与停止支持取消与限时收尾。 |

- **选择与枚举**：`AppRuntime` 持有并释放 `IAudioDeviceManager`。设置页从快照读取设备清单，也有设置窗口专用的纯本地 `audio_list_devices` 桥接命令。实时采集和麦克风自检使用配置的输入设备；原生 TTS 读取配置的输出设备。可选系统默认、枚举项或手动标识；设置页提示蓝牙免提双向模式可能降低音质。更改输入设备会停止实时语音，须手动重启。
- **输出切换**：设备管理器轮询发现正在使用的输出断开或默认输出路由变更时，终止受影响的当前朗读并复位播放状态；下一次播放按配置重新打开，不自动重放。
- **输入恢复边界**：`AudioDeviceRecoveryPolicy` 已独立实现并通过策略单测，但输入热插拔或系统睡眠/唤醒尚未接入自动重开麦克风的桌面链路。须先接入三平台真实电源/设备通知并完成真机验证；用户主动停止后不得自动开麦。
---

## 2. H1 本地离线语音引擎评估（`whisper.cpp` vs `sherpa-onnx`）

Nori.Desktop 2.1 在 `Nori.Core/Voice/Stt` 中定义了 `ILocalSttEngine`、`LocalOfflineSttProvider` 与 `SttProviderRegistry` 扩展接缝，以及 `ProcessLocalSttEngine` / `WhisperCppProcessEngine` 受控进程适配器。**`AppRuntime` 已注册本地离线接缝，但不内置本地引擎二进制或模型权重，也不自动下载任何模型文件。** 以下基于架构特性与工程约束对两个主流开源方案进行评估：

### 2.1 核心指标对比

| 评估维度 | `whisper.cpp` | `sherpa-onnx`（Next-gen Kaldi） |
| --- | --- | --- |
| **开源许可证** | MIT（对闭源/开源分发均极其友好） | Apache-2.0（允许商业与闭源分发，需保留版权与 NOTICE） |
| **中文与中英混合准确率** | `small` / `medium` / `large-v3-turbo` 在中英混说、标点恢复与抗口音方面表现强，整句语义上下文修正能力好；`tiny`/`base` 中文错字偏多。 | Paraformer（中英双语）/ SenseVoiceSmall 在中文、中英混排和口语短句上表现优异，且针对中文声学建模做过专项优化；Zipformer 流式双语模型适合实时字幕。 |
| **实时流式能力** | 原生以 30s 音频窗口编码器为主，非原生流式；做流式需依赖外部 VAD 分片 + 滑动窗口重解码，易产生抖动与重复算力。 | **原生支持低延迟流式识别**（Zipformer / Emformer 流式模型 + 内置 Silero VAD），支持逐帧输入与增量输出，可直接对接 `SupportsRealtimePcm` 与 `SupportsIncrementalDelta`。 |
| **CPU / GPU 可用性** | CPU 支持 AVX2 / NEON / OpenMP；GPU 支持 Metal（macOS 极佳）、Vulkan（跨平台 Win/Linux）、CUDA。 | 基于 ONNX Runtime：CPU 支持 AVX2 / NEON；GPU 支持 CUDA、DirectML（Windows 通用）、CoreML（macOS）。 |
| **模型加载时间（估算）** | GGUF 单文件 + `mmap` 映射：CPU 冷加载通常约 `0.2s ~ 1.5s`（`base`/`small` 量级，受 SSD 与 GPU shader 编译影响）。 | ONNX 模型图初始化：Zipformer/Paraformer 通常约 `0.3s ~ 2.0s`（取决于 ORT Execution Provider 图优化耗时）。 |
| **常驻内存占用（估算）** | `tiny/base (q5)` 约 150~300 MB；`small (q5)` 约 400~700 MB；`large-v3-turbo (q5)` 约 1.2~1.8 GB。 | 流式 Zipformer 双语模型约 150~350 MB；SenseVoiceSmall（int8）约 300~550 MB；Paraformer 非流式约 400~800 MB。 |
| **首字与整句延迟（估算）** | 无原生流式首字输出；在主流 6 核桌面 CPU 上，`small-q5` 对 3~5 秒语音整句转写通常约 `0.5s ~ 1.8s`，GPU（Metal/Vulkan）可压至 `0.15s ~ 0.5s`。 | **流式 Zipformer 首字延迟约 `150ms ~ 350ms`**；SenseVoiceSmall / Paraformer 对 3~5 秒整句在 CPU 上通常约 `0.2s ~ 0.8s`。 |
| **三平台支持情况** | Windows x64、Linux x64/arm64、macOS x64/arm64 均成熟；C ABI 稳定，无重型外部框架依赖。 | Windows x64、Linux x64/arm64、macOS x64/arm64 均提供官方预编译库；需随扩展包携带对应平台的 `onnxruntime` 与 `sherpa-onnx-c-api` 动态库。 |
| **分发体积（运行时 + 模型）** | 运行时极小（约 2~10 MB，CUDA 版除外）；模型从 ~75 MB（`tiny`）到 ~550 MB（`large-v3-turbo-q5`）或 ~1.5 GB 以上。 | 运行时约 25~60 MB（含 ONNX Runtime CPU）；流式双语模型约 80~200 MB，SenseVoiceSmall int8 约 230 MB。 |

> 注：上述内存与延迟区间为依据公开模型参数量、量化位数及运行时特性的工程估算值；具体桌面硬件（尤其核显 Vulkan/DirectML 与低功耗 ARM 设备）的精确表现需在目标真机实测校准。

### 2.2 推荐分阶段接入方案

1. **阶段一（当前 2.1 已完成注册表运行时接线与单测，待挂载外部引擎与模型）：扩展接缝 + 受控外部进程适配器**
   - 保持主安装包轻量化，不绑定任何本地推理二进制或模型权重；`AppRuntime` 通过注册表装配 `LocalOfflineSttProvider` 接缝，仍需挂载外部引擎和模型。
   - `SttProviderRegistry` 严格依据能力与隐私策略路由，拒绝 `Mode == Local` 且未声明 `SupportsLocalOffline` 或矛盾声明 `RequiresNetwork`/`UploadsAudioToCloud` 的注册；`LocalOfflineSttProvider` 如实保留底层引擎 `SupportsLocalOffline` 声明并拒绝非离线引擎执行。
   - 提供 `ProcessLocalSttEngine` 与 `WhisperCppProcessEngine` 作为受控外部进程适配器：仅使用 `ProcessStartInfo.ArgumentList`（防命令注入）；在任何 `File.Exists`/`Directory.Exists` 探测前严格拒绝 UNC/网络路径（`\\server\share`、`//server/share`、`\\?\UNC\...`、`file://` 等）及解析目标指向 UNC/网络路径的符号链接；取消或超时时先终止进程树并有界等待子进程退出释放文件句柄，再在 `finally` 中对临时 WAV 文件执行尽力零值覆写并删除（注：用户态覆写属于尽力清理，在现代 SSD 磨损均衡或 CoW 文件系统上不构成物理扇区不可恢复销毁保证），若最终删除失败则通过 `TempFileCleanupFailed` 事件、回调与 `LastCleanupFailure` 状态记录暴露而非静默吞掉。
   - **安全边界诚实说明**：宿主侧本地离线适配器仅运行用户显式配置的可信离线引擎二进制，宿主自身不发起任何网络请求，但路径与参数校验不等同于对任意第三方外部二进制的操作系统级网络沙箱。
2. **阶段二（流式离线优先接入）：`sherpa-onnx` 可选扩展包**
   - 以独立可选扩展/插件包形式分发 `sherpa-onnx` C-API 原生库与模型管理工具，优先适配中文/中英混合场景（流式 Zipformer 负责低延迟实时增量字幕 `SupportsRealtimePcm = true` + `SupportsIncrementalDelta = true`；SenseVoiceSmall/Paraformer 负责高精度整段复核）。
   - 直接复用 `LocalOfflineSttProvider` 对 `ILocalSttEngine.Capabilities` 的动态透传机制，无需改动上层 `RealtimeVoiceSession` 状态机。
3. **阶段三（高精度/GPU 桌面增强）：`whisper.cpp` 原生库与多语种补充**
   - 面向高算力 GPU（macOS Metal、Windows/Linux Vulkan 或 CUDA）或小语种（日语/多语混杂）场景，将 `whisper.cpp` 以原生 FFI 或插件形式接入 `ILocalSttEngine`，由 `SttProviderRegistry` 按 `SupportsHardwareAcceleration` 与用户配置择优路由。

---

## 3. H3 后续联动边界

在后续版本规划语音能力与桌面伴侣其他子系统的联动时，必须严格遵守以下模块边界与权限红线：

1. **Linux 情境感知（`UniDesktop` / 窗口焦点 / 空闲状态）**
   - 仅可用于在**用户已打开聊天或开启伴侣交互**的前提下，向用户展示非侵入式的快捷入口提示（例如在全屏游戏/会议时建议用户暂停实时语音，或根据系统空闲暂停无意义的自动朗读）。
   - **严禁**因为检测到特定应用窗口或用户活动而自动调用 `RealtimeVoiceSession.StartAsync()`。
2. **本地 AI 模型管理（模型目录与硬件探测）**
   - 可与模型管理页面联动提供本地 STT 模型的手动导入、SHA-256 校验、磁盘占用统计及 `LocalSttModelConfig` 绑定；模型文件统一存放在 `<PackageRoot>/data/resources/` 下受控路径，延续 `ZipExtractor` / 路径防穿越规范。
   - `--safe-mode` 下禁止触发任何远端离线模型包下载。
3. **学习陪伴模式（专注计时与口语/复述练习）**
   - 可在用户主动进入学习/口语练习面板并点击开始录音后，借助 `SttProviderRegistry` 选择支持 `LanguageHint` 或本地离线的 STT 提供商完成语音输入与复述反馈。
4. **不可逾越的隐私与采集红线**
   - **未获用户主动授权与显式操作，绝不自动打开麦克风或进行任何形式的后台自动录音。**
   - 所有后续联动不得绕过 `CaptureOwnership` 互斥锁，不得将原始音频写入持久磁盘缓存、数据库或诊断 ZIP。

---

## 4. 三平台实机回归清单

### 4.1 已由自动化测试覆盖项

以下命令在 `app/desktop/` 下通过 `.NET 10` 测试套件确定性验证（不依赖真实声卡硬件、外部模型权重或真实云端 API）：

- **连续音频处理与重采样**：
  - `ContinuousAudioPipelineTests`、`PcmResamplerTests`：跨块残余帧对齐、多声道下混、任意回调块大小、16k/24k/44.1k/48k 连续线性重采样无相位漂移、非有限/超幅值钳位与平滑电平归零。
- **VAD 与有界队列**：
  - `EnergyVoiceActivityTests`、`RealtimeVoiceSessionTests`：能量检测灵敏度、静音超时收尾、前置环形缓冲、`BoundedAudioQueue` 容量上限与溢出保护。
- **OpenAI Realtime GA 与事件背压**：
  - `OpenAiRealtimeSttTests`、`RealtimeStreamingSttIntegrationTests`：`OpenAiRealtimeSttUriBuilder` 安全校验（拒绝非环回明文/URL 凭据/非法查询参数）、`session.update` 握手、24kHz PCM16 帧分片与提交、`BoundedSttEventChannel` 背压、超时关闭与 `SttSanitizer` 密钥脱敏。
- **提供商契约与能力注册表**：
  - `SttProvidersTests`：`OpenAiRealtimeSttProvider`、`GptTranscribeProvider`（含 `whisper-1` 降级）、`WhisperSttProvider` 协议与异常契约（已接入 `AppRuntime`）。
  - `LocalOfflineSttAndCapabilitiesTests`：`SttProviderRegistry` 能力路由与零名称硬编码验证、拒绝矛盾本地/非离线声明、`AllowNetwork = false` 严格本地且零云上传过滤、`LocalOfflineSttProvider` 如实保留底层离线声明并拒绝非离线引擎、`ProcessLocalSttEngine` 拒绝 UNC/网络路径与指向 UNC 的符号链接（零提前文件探测）、取消/超时等待子进程退出后再执行尽力覆写与删除、以及删除失败可观测暴露（外部进程适配器仍未挂载到桌面运行时）。
- **会话状态机、桥接与原生长链路**：
  - `RealtimeVoiceSessionTests`：代号递增防陈旧、打断与播放期 `CapturePaused` 护栏、失败前自动回退整段转写、草稿/文本零泄露状态字段。
  - `RealtimeVoiceBridgeTests`：原生桥接命令路由、指定输入设备、设置页枚举、STT 注册表装配与安全模式拦截。
  - `VoiceConversationIntegrationTests`、`VoiceSecurityAndPrivacyTests`：模拟流式转写与首段播放指标、设备重开失败、语音 Agent 首个增量与诊断 ZIP 隐私。
  - `NativeAudioPlaybackTests`、`NativeMicrophoneRecorderTests`、`WaveDecoderTests`、`VoiceAudioTests`、`VoicePipelineTests`、`VoiceProviderTests`、`VoiceServiceTests`、`VoiceTextSanitizerTests`：WAV 解码、有界合成预取流水线、LRU 缓存与文本清洗。
  - `WasapiAudioDeviceTests`、`CoreAudioDeviceTests`、`AlsaAudioDeviceTests`、`NativeAudioFactoryTests`：三平台设备适配器生命周期、异常处理与工厂分发逻辑（含模拟/接口层测试）。

### 4.2 需人工实机验证项

以下场景依赖真实操作系统音频栈、硬件驱动、系统权限弹窗或真实公网端点，无法仅靠单元测试替代，须在发布前于三平台实机回归：

1. **Windows 10/11（`win-x64`）实机验证**
   - [ ] 默认麦克风与扬声器（WASAPI 共享模式）在连续对话 5 轮以上时的采集稳定性、无爆音/漂移与口型同步。
   - [ ] 蓝牙耳机（如 AirPods / Sony WH 系列）连接与通话：切换 HFP 免提麦克风与 A2DP 播放时，采样率切换下 `ContinuousAudioProcessor` 是否正常收敛且不崩溃。
   - [ ] 实际列出并选择指定输入/输出设备、切回系统默认；变更默认输出或断开选定输出时确认当前朗读安全停止，下一次播放使用正确路由。
   - [ ] 拔掉唯一麦克风或禁用录音权限时，UI 是否在聊天栏展示清晰中文提示并安全回到 `Error`/`Idle`，不卡死窗口。
2. **macOS（`osx-arm64` / `osx-x64`）实机验证**
   - [ ] 从 `.app` 包启动时，`Info.plist` 中 `NSMicrophoneUsageDescription` 正确触发系统麦克风授权弹窗；拒绝授权后重新开启实时语音或测试麦克风，确认给出友好中文提示且不崩溃。
   - [ ] CoreAudio AudioQueue 采集与播放连续交替运行，UI 线程无阻塞卡顿（停止采集与打断响应 < 300ms）。
   - [ ] 内建麦克风与外接 USB/蓝牙音频设备之间切换后，输入电平条与口型驱动正常。
3. **Linux（`linux-x64` / `linux-arm64`）实机验证**
   - [ ] PipeWire（`pipewire-alsa`）与纯 ALSA 环境下 `default` 设备的连续录音与短音频播放，确认短音频播完收尾不卡死。
   - [ ] KDE Plasma Wayland / GNOME Wayland 桌面下，聊天窗口实时语音栏刷新、快捷键/按钮打断与桌宠透明穿透无互相干扰。
   - [ ] 蓝牙耳机切换 HSP/HFP 配置文件后重新开始监听，确认采集与重采样正常。
4. **跨平台系统休眠与真实端点验证**
   - [ ] 三平台在开启或暂停实时语音后使设备进入睡眠/合盖休眠再唤醒，验证现有链路安全回到可手动重试状态，不残留僵尸线程或占用锁；主动停止后重新插入设备及唤醒不能自动开麦。
   - [ ] 配置真实 OpenAI API Key 验证 `wss://api.openai.com/v1/realtime?intent=transcription`（`gpt-live-transcribe`）增量字幕、提交与打断；并在人为断网/断开代理时验证超时释放、提交前批处理回退与「将转写追加到草稿」草稿保护功能。
   - [ ] **（待外部引擎包挂接及本地模式启动路径开放后执行）**接入真实本地 `whisper.cpp`（或外部 `ILocalSttEngine` 扩展）配合真实模型权重在三平台实测冷加载耗时、内存峰值与端到端延迟，并确认 `--safe-mode` 与断网状态下可完整离线识别且临时音频目录零残留。
