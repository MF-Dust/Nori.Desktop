# Nori Plugin Specification 2.0

本文记录 Nori Desktop 当前插件基础设施与本地插件管理边界。插件是受信任的 .NET 进程内扩展；`AssemblyLoadContext` 只用于依赖隔离和卸载尝试，**不是安全沙箱**。

## 项目边界

插件相关的生产代码统一位于 `app/desktop/Nori.PluginRuntime/`，只生成一个程序集：

```text
Nori.PluginRuntime
├── 插件作者可见的合同：INoriPlugin / IPluginContext / Contribution / Capability / WebView
├── manifest、SemVer、包安装与路径安全
├── 生命周期、可回收 AssemblyLoadContext 与故障恢复
├── 插件 WebView、隔离 bridge 与管理命令
└── PluginRuntimeHost 与 AssetServer 附加资源路由
```

`Nori.Desktop` 只负责装配 `PluginRuntimeHost`、注册资源路由并把宿主窗口来源适配给 Bridge 路由。`Nori.Core` 只提供通用资源服务与文件路径安全能力，不包含插件策略。

测试夹具保持独立：`Nori.PluginRuntime.Tests` 验证运行时，`Nori.PluginRuntime.TestPlugin` 是用于真实 ALC 加载的测试插件，不会进入发布包。

第三方插件必须面向 `Nori.PluginRuntime` 的公共合同编译。标准获取方式是 NuGet 上的 [`Nori.PluginSDK`](https://www.nuget.org/packages/Nori.PluginSDK/)；不要直接引用 Nori Desktop 仓库里的宿主项目，也不要复制宿主程序集。运行时实现类型均为内部类型，插件只能编译期看到明确的公共合同。旧的多个合同/运行时程序集不再发布，也不提供类型转发兼容层。

## 插件 SDK

Plugin API 2.0 对应 `Nori.PluginSDK` 2.0.0，目标框架为 .NET 10。新插件可以直接安装 NuGet 包：

```bash
dotnet add package Nori.PluginSDK --version 2.0.0
```

或在项目文件中声明：

```xml
<ItemGroup>
  <PackageReference Include="Nori.PluginSDK" Version="2.0.0" />
</ItemGroup>
```

`Nori.PluginSDK` 是仅编译期使用的 ref-only 包。NuGet 包 ID 为 `Nori.PluginSDK`，其中合同程序集名称仍是 `Nori.PluginRuntime`；包只提供 `ref/net10.0/Nori.PluginRuntime.dll`，没有 runtime asset。插件运行时由 Nori Desktop 提供自己的 `Nori.PluginRuntime` 宿主程序集。

因此 `.noripack` 不得携带 `Nori.PluginRuntime.dll`。插件作者无需克隆 SDK 仓库或使用跨仓库 `ProjectReference`；SDK 源码、示例与更完整的作者指南位于 [`MF-Dust/Nori.PluginSDK`](https://github.com/MF-Dust/Nori.PluginSDK)。

## 核心合同

- `INoriPlugin`：`ActivateAsync(IPluginContext, CancellationToken)` 与 `DeactivateAsync(CancellationToken)`。
- `PluginDescriptor`、`IPluginContext`、`IPluginLogger`、`IPluginStorage`、`IPluginAssets`。
- `IPluginContribution`、`IContributionRegistry` 与幂等的 `IPluginRegistration`。
- `IPluginCapability`、`IPluginCapabilities`、`PluginCapabilityStatus` 与 `[PluginCapability]`。
- `IWebViewCapability`、`PluginWebViewOptions` 和 `IPluginWebViewWindow`。

插件不引用 `Nori.Core`、`Nori.Desktop`、`AppServices`、`IServiceProvider`、Bridge、窗口管理或业务运行时。`Nori.PluginRuntime` 程序集中的宿主实现由内部可见性控制，不能作为插件能力使用。

当前唯一真实宿主 capability 是 `ui.webview`。没有 Arcade、Games 或 Harness 公共合同；这些尚未落地的领域不会以空壳 API 进入插件边界。

## manifest.json

最小示例：

```json
{
  "schemaVersion": 1,
  "id": "io.nori.example",
  "name": "Nori Example",
  "description": "Nori plugin",
  "version": "1.0.0",
  "authors": [{ "name": "Nori" }],
  "homepage": "https://example.invalid",
  "repository": "https://example.invalid/repo",
  "license": "MIT",
  "apiVersion": "2.0",
  "minHostVersion": "1.0.0",
  "runtime": {
    "kind": "dotnet",
    "assembly": "lib/Nori.Example.dll",
    "entryType": "Nori.Example.Plugin"
  },
  "ui": { "webRoot": "web" },
  "capabilities": ["ui.webview"],
  "optionalCapabilities": [],
  "platforms": ["windows", "linux", "macos"],
  "dependencies": []
}
```

`PluginManifestReader` 在创建 ALC 前检查：

- `schemaVersion` 必须为 `1`；插件 SemVer、API `major.minor` 与 schema 是独立概念。
- `id` 必须匹配 `^[a-z0-9]+(\.[a-z0-9_-]+)+$`；`runtime.kind` 当前只允许 `dotnet`。
- `runtime.assembly` 必须是 `lib/` 下的 `.dll`；`entryType` 不接受 assembly-qualified 字符串。
- `ui.webRoot` 必须位于 `web/` 下；列表不能重复，required/optional capability 不能交叉。
- 依赖使用 `<id>` 加空格分隔的 `>=`、`>`、`<=`、`<`、`=` 约束，例如 `>=1.0.0 <2.0.0`。
- 重复 JSON 属性、非法路径、未知 schema 与其它解析失败都转换为带稳定 `Code` 的 `PluginException`。

插件 API 2.0 的兼容规则是 Host major 必须等于插件 major，且 Host minor 不小于插件 minor。1.x 插件可以被发现、展示和卸载，但会标记为 `plugin.incompatible_api`，不会创建 ALC 或执行入口。

## `.noripack` 与安装目录

`.noripack` 是 ZIP，允许的布局为：

```text
manifest.json
README.md       (可选)
LICENSE         (可选)
icon.png        (可选)
lib/            (托管入口与插件私有依赖)
web/            (公开 Web 资源)
assets/         (公开资源)
locales/        (公开资源)
runtimes/       (插件私有运行时依赖)
```

宿主数据目录使用包根 `<PackageRoot>/data` 的固定插件布局：

```text
<data>/plugins/
├── cache/packages/inbox/*.noripack
├── temp/staging/<temporary>/
├── cache/webview/<pluginId>/
├── data/<pluginId>/storage.json
└── installed/<pluginId>/
    ├── current.json
    ├── 1.0.0/
    └── 1.1.0/
```

`AppStoragePaths` 不允许插件运行时回退到安装目录之外；插件资源与 WebView 缓存也不会进入发布包。

安装顺序是包预检、同卷 staging、安全解压、manifest/入口/引用复校验、版本目录移动和 `current.json` 原子替换。ZIP Slip、绝对路径、`..`、控制字符、重复规范化路径、符号链接、单文件/总展开大小及压缩比均拒绝。包中不得携带 `Nori.PluginRuntime.dll` 或旧合同程序集副本。

设置页的本地安装只允许宿主 Avalonia `StorageProvider` 文件选择器产生 `.noripack` 路径。前端传入的任意路径参数会被忽略。首次继续安装前会提示插件属于 trusted in-process 扩展；确认只记录在主 WebView 的 localStorage。

新安装默认 `enabled=false`，不会自动加载或执行第三方 DLL。既有安装若没有状态记录，则按兼容语义视为启用。当前没有联网 Marketplace、签名系统或自动更新；活动版本更新无法安全回收当前 ALC 时由重启完成切换。

## Runtime 生命周期

```text
manifest/schema/API/platform/dependency/capability validation
  -> Discovered / Loading
  -> collectible PluginLoadContext + AssemblyDependencyResolver
  -> 精确加载 manifest.runtime.entryType
  -> 插件专属 IPluginContext
  -> ActivateAsync
  -> Active / 可枚举 Contribution

显式停用/禁用
  -> Stopping
  -> revoke stopping token / contribution / capability lease
  -> 关闭该插件全部 WebView
  -> DeactivateAsync / cleanup
  -> 清除实例、context 与委托引用
  -> ALC.Unload + 有界 GC 检查
      -> Installed/Disabled
      -> 无法回收时 PendingRestart
```

`PluginRuntimeHost` 是唯一装配入口，拥有 `PluginManager`、动态 WebView 窗口、管理命令和插件资源路由。`PluginStateStore` 保存用户启用意图以及待重启卸载请求；`plugin-startup.json` 只承担启动失败恢复。连续启动失败会保护性禁用，显式重新启用时清除该保护后重试。

激活、停用、贡献调用和 WebView bridge 回调均在宿主边界包装。日志只记录插件 ID、版本、阶段和稳定错误码，不把参数、结果、请求正文、完整路径或 stack trace 暴露给前端 DTO。

## 主设置页插件管理

宿主命令固定为：

```text
plugin_list
plugin_install_local
plugin_enable({ id })
plugin_disable({ id })
plugin_uninstall({ id, deleteData })
```

这些命令只允许当前可见的 `main` WebView 调用。`plugin_list` 只做无 ALC 的 refresh/discover；安装取消返回 `{ cancelled: true }`；启用、禁用和卸载返回最新脱敏 DTO。

DTO 只暴露 manifest 的公开元数据、固定 state 字符串、用户 enabled 意图、capability status、稳定错误码、脱敏错误文本、requiresRestart 与公开 icon URL。不会序列化安装路径、插件数据路径、ALC、context 或异常对象。

固定 state 字符串为：

```text
installed
loading
active
stopping
disabled
failed
incompatible
pending_restart
```

Safe Mode 允许列出、禁用和卸载；拒绝本地安装与启用。Safe Mode 不覆盖 `plugin-state.json` 中保存的用户启用意图，且不会创建 ALC、插件存储或执行第三方入口。

## Plugin WebView 与 bridge

插件窗口由 `PluginWindowHost` 管理，不加入固定窗口定义。标签固定为 `plugin:<pluginId>:<windowId>`，身份由宿主创建时绑定，页面载荷里的 `pluginId` 永远不是权限依据。所有插件 ID 使用 manifest 的统一校验规则。

插件页面的 bridge 不转发到宿主 `NoriBridge`，只接受以下五个固定命令：

```text
plugin_get_info
plugin_get_capabilities
window_get_info
window_close
ping
```

除此之外，插件可以在创建窗口时通过 `PluginWebViewOptions.CommandHandler` 注册自己的 `IPluginWebViewCommandHandler`，页面发起的非白名单命令会被转发给该处理器（deny-by-default：未注册处理器时一律拒绝）。白名单命令始终优先于自定义处理器。

插件还可以注册 `IPluginActionContribution` 贡献（Id/Description/参数 Schema/InvokeAsync）。宿主在活跃插件变化时把这些动作注册进 `ToolRegistry` 的 `plugin` 分类（工具名 `plugin__<pluginId>__<actionId>`，权限级别 safe），伴侣 AI 对话即可调用；动作内部经 `IPluginWebViewWindow.SendEventAsync` 向页面推送事件（`kind:'event'` 信封），由页面执行实际播放控制。桥接使用独立的 `window.__noriPlugin.dispatch` 信封；旧点号/短名称别名、安装/启用/禁用/卸载以及宿主核心命令均拒绝。插件窗口使用独立的 WebView 数据目录，插件上下文撤销时自动关闭。

## 插件聊天卡片 (约定)

活跃插件包内存在 `web/card.html` 时, 宿主聊天界面顶部的通用卡片槽会以同源 iframe
挂载 `/<random-prefix>/plugins/<pluginId>/web/card.html` (标题取插件名)。
卡片内通过 `window.parent.postMessage` 请求宿主代理调用插件动作:

```text
卡片 → 宿主: {source: "nori-plugin-widget", requestId, pluginId, actionId, args}
宿主 → 卡片: {source: "nori-plugin-widget-host", requestId, result, error}
```

宿主代理只转发到 `plugin_action` (仅主窗口, 即活跃插件的 IPluginActionContribution)。
卡片 UI 与文案完全由插件自带, 宿主不含任何具体插件逻辑。

## AssetServer 附加路由

Core 的 loopback `AssetServer` 只负责通用服务：随机前缀、Host allowlist、`/app`、`/nori-assets`、`/media` 和可注册的 `IAssetRoute`。扩展模块通过 `AdditionalRoutes` 注册路由，Core 不知道插件 ID 或 capability 策略。

插件运行时注册：

```text
/<random-prefix>/plugins/<pluginId>/web/index.html
```

`PluginAssetRoute` 负责 manifest ID、公开目录 allowlist、containment 与 symlink/reparse 检查；Core 负责统一文件响应。生产 URL 使用随机前缀同源地址，开发 URL 使用 `/plugins/...` 并由 Vite 代理到同一回环服务。不启动第二个静态服务器，`vite.config.ts` 继续使用 `base: "./"`。

## 当前未实现

- Marketplace、联网下载/安装、签名与供应链验证、自动更新。
- WASM 与 out-of-process 插件沙箱。
- Arcade、Games、Harness runtime 及对应 WebSocket/world/patch/审批 adapter。
- 完整插件前端 SDK、动态 Vue 注入、插件直接 Avalonia Control。
- 任意 network/filesystem/shell/process/LLM/memory/MCP/automation/pet/chat capability。
