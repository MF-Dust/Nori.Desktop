# PR #63 Codacy 处理记录

基于 `3e6ada9112b383f118ccf4bf8b7a15e65c97d584`，读取 [Codacy 检查](https://github.com/MF-Dust/Nori.Desktop/runs/105652576349) 的全部 20 条注释，并核对 AI 总评。未读取或合入 PR #60，未修改 `components.d.ts`，未加入 Windows 非 WAV 自动 WebView 回退。

## 逐项处理

| 原始位置 | 条数 | 处理 |
| --- | ---: | --- |
| `AiDraft.ApiKey` | 1 | 使用明确的 `string.Empty` 初始状态并说明不内置凭据。原值为空字符串，未发现实际硬编码密钥。 |
| `NativeAudioPlayback.Pump` | 1 | 使用 `while` 表达按设备实际写入量推进的循环，保留部分写入与取消行为。 |
| `PluginWidgetHostTests` | 1 | 未知测试场景立即抛出参数异常。 |
| 两个 `PropertyKey`、`WaveFormatEx` | 3 | 实现完整值比较与哈希，测试固定结构尺寸和字段偏移。 |
| 两个 `PropVariant` | 2 | 类型级 `S3898` 精确抑制并注明理由。原生联合体包含待释放指针，不能把指针相同定义为内容相等；保留 ABI 和原生释放路径。 |
| `HString` | 1 | 改为密封引用所有者，用原子交换保证共享引用或重复 Dispose 只释放一次。 |
| `PluginWidgetHost` 的 `_` 参数赋值 | 2 | 使用具名事件参数，任务赋值恢复为真正的 discard。 |
| `InitWindow` 的 `_` 参数赋值 | 1 | 使用具名事件参数，异步启动的 discard 不再写入事件参数。 |
| `AppRuntime.Notifications` | 1 | 明确忽略未知通知动作，继续保持授权默认拒绝。 |
| 回环 HTTP 用户信息拒绝测试 | 1 | 改为独立测试，通过 UriBuilder 添加用户信息后验证拒绝。测试不发出请求，回环服务 HTTP 契约不变。 |
| Vue 回调与卸载检查 | 2 | void 回调使用语句块，异步返回后重新读取组件生命周期，保留卸载后丢弃结果的行为。 |
| 命令映射中的 `void` | 1 | 用函数返回类型别名表达无业务结果，保持 `Promise<void>`，不假定桥接返回 undefined。 |
| 卡片列表的冗余 `?? []` | 1 | 后端保证数组返回，移除与契约不符的空值回退。 |
| `contentWindow!` | 1 | 显式读取、判空并复核消息窗口身份。 |
| opaque origin 的 `postMessage("*")` | 1 | 保留公开插件协议所需的目标写法；没有全局关闭安全规则。每次发送前检查绑定、连接状态与来源窗口，并新增再次加载后撤权及丢弃旧回包。此条仍可能被 Codacy 标记，见下方边界。 |

## AI 总评中的实质问题

WASAPI 软件音量使用 `Volatile.Read/Write`，每批样本读取一次快照，更新不等待可能阻塞的设备锁。拒绝 NaN 与无穷值，有限值夹到 0..1；播放入口同样先校验再改变状态。新增阻塞写入期间跨线程改音量的确定性测试，覆盖浮点与 PCM16。

重采样改为独立 Blackman 窗 sinc 低通实现，截止频率随较低采样率缩放，使用预计算核查表避免热循环三角函数。保留同采样率精确路径、声道映射及独立下混电平轨，转换中检查取消。新增通带、降采样阻带、升采样镜像、长片段帧数、边界及取消用例。

检查互操作代码时同时修复设备名称读取：仅对 `VT_LPWSTR` 解引用，始终释放属性存储 COM 引用。新增联合体标签和布局测试。

## 安全边界

iframe 保留 `sandbox="allow-scripts"`，不开放 `allow-same-origin`。opaque origin 无法用具体 URL 作为 postMessage 的目标 origin，故不能直接把 `*` 改成 URL 或字符串 `null`。现有消息协议还存在导航开始到后续 load 事件之间的竞争窗口；WindowProxy 身份不随导航改变，本轮的加载撤权不能证明该窗口完全消失。若要关闭这一协议边界，需要协调插件 SDK 改用文档绑定的 MessagePort 通道，不能靠删除隔离属性或伪造安全扫描通过来处理。

真实 WASAPI、Windows HSTRING 和浏览器导航仍需对应平台运行验证。新增测试没有删除原失败用例，也没有降低覆盖率阈值。

## 验证

- `pnpm lint`、`pnpm build`、`pnpm test` 通过，32 个文件、180 项测试。
- `pnpm coverage` 通过阈值，statements 56.78%、branches 51.42%、functions 51.11%、lines 58.35%。
- Codacy 对应的四条 TypeScript ESLint 严格规则定向验证通过。
- `pnpm check:todo` 和 `git diff --check` 通过。
- 已安装 .NET SDK 10.0.401；独立无外部包项目链接真实重采样源码的 Release 验证通过。四组通带最大 RMSE 0.0000327，四组降采样阻带最大 RMS 0.00001855；44100→48000 的十秒片段得到精确的 480007 帧，直流幅度保持。
- `dotnet test Nori.Core.Tests/Nori.Core.Tests.csproj --configuration Release --no-restore -m:1` 通过，1153 项、无失败或跳过，含新增 24 项音频用例。
- 独立无外部包项目链接两个 NativeApi、WasapiAudioDevice 和对应测试源文件，Release 且警告视作错误编译通过；精简断言运行器的 29 项检查通过。Windows HSTRING 真实句柄测试在 Linux 未执行，此验证不替代完整 Desktop xUnit。
- Desktop 项目 Release 构建及完整 xUnit 通过：636 项通过、11 项跳过、0 失败。跳过项为原有模型/视觉环境用例，以及新增的 Windows HSTRING 原生句柄测试。
- `dotnet build Nori.slnx --configuration Release --no-restore -m:1 -nr:false` 通过，0 警告、0 错误。其余 PluginRuntime 166 项、Launcher 5 项测试全部通过；四套后端测试合计 1960 项通过、11 项平台/环境跳过、0 失败。

本地 MSBuild 默认并行还原曾无诊断退出；改用 `-m:1 -nr:false --disable-parallel` 后完整还原与构建通过。Windows/macOS 运行结果及 Codacy 是否消除对应告警仍以新提交的 CI 为准。

原提交的 Windows/Linux/macOS build 工作流已经通过。独立 `github-advanced-security` 检查的失败日志为 `CAPIError: 400 The requested model is not supported`，属于 GitHub 审查服务模型错误；本轮没有通过修改代码或工作流隐藏该失败。
