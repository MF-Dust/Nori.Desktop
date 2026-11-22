# Nori.Desktop 2.1 · Realtime STT 落地计划（2026-11-22）

> 状态：执行中；本文记录工程计划和验收，不代表全部代码已经完成。基线：main 已合并 #77、#80、#81。

## 0. 现状与目标

现状：`RealtimeVoiceSession` 已实现本地能量 VAD、最长 30 秒分段、取消代号、连续监听、打断与原生 Agent/TTS 交接。当前 `WhisperUtteranceTranscriber` 只在分段结束后提交完整 WAV，`SupportsStreaming = false`。禁止把“分段上传”冒充“实时转写”。

目标：支持可选的真正流式 STT：**采集期间**传输 PCM 音频，并显示服务端 delta；**最终文本**确认后才能触发一次 Agent。默认保持 Whisper、现有授权规则及安全模式。

## 1. 技术决策

- 第一提供商：OpenAI transcription session + WebSocket + `gpt-live-transcribe`。服务端输入使用单声道 24 kHz PCM16，禁用服务端自动 turn detection，继续以本地 VAD 决定 commit。后续提供商以独立适配器实现。
- 仅支持有能力的后端；不能假定目前配置的 `stt_base_url` 或代理兼容 Realtime WebSocket。自定义端点在能力验证前不自动转写成 `wss://`。
- 控制平面：`session.update`，等待服务端 session 确认后才接受上行音频；数据平面：`input_audio_buffer.append`；分段完成：`input_audio_buffer.commit`。
- 接收 `conversation.item.input_audio_transcription.delta` 及 `...completed`，用 `item_id` 对齐归属；多轮完成可能乱序，绝不能用到达顺序替代身份。
- partial 仅供 UI 展示，且允许撤回与重写。Agent 只消费合法且未取消的 final，一轮至多一次。
- 不在日志、诊断、历史中记录音频原文、识别中间结果、API Key 或 WebSocket 报文。
- 采用显式选择和开关。开启“允许实时语音”本身不打开麦克风，进入流式 STT 后需明确知道实时音频将持续上传并可能产生费用。
- 播放期间暂停采集，保持尚未实现回声消除和语音打断的真实能力说明。

## 2. PR #82：传输与协议层（本 PR）

### A. 协议对象与可替换网络边界
- [ ] 定义 `IRealtimeTranscriptionSocket`，隔离具体 WebSocket 与事件解析，方便无外网单元测试。
- [ ] 使用官方 transcription session 格式连接和发送配置；有界帧长度与输入检查。
- [ ] 正确处理 session ready、delta、committed item、completed、error、close 和取消。
- [ ] session、socket、token 都由转写服务控制生命周期；取消即关连接，不继续传音频。

### B. 安全与性能
- [ ] 首版只允许可信的 OpenAI Realtime endpoint，证书走系统 TLS 校验；不把密钥拼到 URL。
- [ ] 上行 PCM16 必须是 24 kHz 单声道 little-endian；任意设备率的状态化重采样留到 #83。
- [ ] 单块最长 1000 ms，消息最大 256 KiB，超界拒绝，避免无限积压。
- [ ] 将原始异常细节隔离在 SDK/内部，不把鉴权响应、WebSocket 原始内容和私密参数展示给用户。
- [ ] 禁止后台自动重试已 commit 的音频轮次，以免重复上传或重复 Agent。

### C. 可验证交付
- [ ] 不联网的 fake socket 测试：配置、PCM append、commit、delta、final、item id。
- [ ] 取消、断线、服务器错误、超大消息、不正确帧大小均失败关闭。
- [ ] 通过 .NET 10 三平台编译与 Core 单元测试；CodeRabbit 问题解决后单独合并。
- [ ] 此 PR 暂不改变现有默认 STT 或实际麦克风数据路径；避免误称已上线真流式。

## 3. 后续 PR #83：接入实时采集与原生 UI

### A. 会话编排
- [ ] 将设备采集帧和本地 VAD 分段同时交给音频分发器。16/44.1/48 kHz、多声道转换为连续、无边界丢样的 24 kHz 单声道 PCM16。
- [ ] VAD 发现讲话开始后按需建立流式 session，将 preroll 与实时帧以有界队列发送；语音结束 commit 并等待 final。
- [ ] 以 `generation` + `item_id` 双重校验；取消/打断/停止后忽略迟到的 delta 和 final。
- [ ] 握手失败或不支持实时 STT 时回退现有整段 Whisper；不能在一次已成功送交 Agent 的轮次里重试发送同一段音频。
- [ ] 首版一次最多一个待提交音频轮次，保留现有单活动 Agent 闸门及语音来源逐次授权。

### B. 设置与交互
- [ ] 选择“Whisper 整段识别/流式识别（预览）”，默认 Whisper；安全模式禁用联网。
- [ ] 明示提供商、网络上传、可能费用、麦克风占用和功能限制，不能标记支持语音打断。
- [ ] 原生 ChatView 显示临时字幕和最终结果，字幕不进入草稿/历史，最终文本才触发 Agent。
- [ ] 考虑中英混合与空 transcript，空文本结束不调用 Agent。
- [ ] 状态显示连接、识别、等待 final、网络失败、回退；设置页语言 zh-CN/en-US 同步。

### C. 验证
- [ ] Headless UI：初次聊天加载竞态、字幕节流、旧 generation 延迟事件、取消后不复活。
- [ ] 音频：16/44.1/48 kHz 与单/双声道，帧边界连续性、裁剪、clipping、0 输入，30 秒上限。
- [ ] 网络：握手超时、鉴权失败、断网、中途关闭、重连、重复 final、item_id 不匹配、文本超长。
- [ ] 权限：Trusted/Bypass/Session 下仍逐次批准语音来源工具；Ask 与 safe mode 既有行为不变。
- [ ] 实机：Windows + Linux Plasma Wayland + macOS，对耳麦/蓝牙/系统输入设备与睡眠唤醒进行测试。

## 4. 非目标与后续分支

- 此阶段不加入回声消除 AEC、真实语音 barge-in、说话人身份认证。
- 不改插件 SDK、聊天历史 schema、模型管理接口、默认工作区工具权限。
- 不做“按文字变化推测 delta”的伪流式、不把 Whisper 整段输出切字符伪装字幕。
- 完整麦克风设备选择、WebRTC、其他云厂商兼容性放在后续 PR 评估。

## 5. 验收门槛

1. 默认配置启动行为与 #81 完全一致：没有自动监听，`streaming=false`。
2. 新的传输层已通过 mock 协议测试，能在音频上传过程中接收 delta。
3. 后续 UI 接线通过端到端测试后，才允许 `streaming=true` 和面向用户的真流式文案。
4. 取消不提交旧音频、不补发已完成轮次、不重复工具执行。
5. 失败有可操作的脱敏提示、配置默认可回退至稳定 Whisper。
6. 按 CI 结果逐平台核验 Release 构建、核心测试、桌面 headless 回归与包体冒烟；无 CI 或没有证据不能写“通过”。
7. CodeRabbit 审查无未解决阻塞意见，开发者确认后合并；不自动合并或发布。

## 6. 关键参考

- OpenAI Realtime transcription: https://developers.openai.com/api/docs/guides/realtime-transcription
- OpenAI Realtime WebSocket: https://developers.openai.com/api/docs/guides/realtime-websocket
- 现有 `RealtimeVoiceSession.cs`, `WhisperUtteranceTranscriber.cs`, `NativeMicrophoneRecorder.cs`, `AppRuntime.Chat.cs`
- 基线 #77、#80、#81。
