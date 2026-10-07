# Nori Plugin Specification 2.1

本文记录 Nori Desktop 当前插件基础设施与本地插件管理边界。插件是受信任的 .NET 进程内扩展；`AssemblyLoadContext` 只用于依赖隔离和卸载尝试，**不是安全沙箱**。

## 项目边界

插件相关的生产代码统一位于 `app/desktop/Nori.PluginRuntime/`，只生成一个程序集：

```text
Nori.PluginRuntime
├── 插件作者可见的合同：INoriPlugin / IPluginContext / Contribution / Capability
├── manifest、SemVer、包安装与路径安全
├── 生命周期、可回收 AssemblyLoadContext 与故障恢复
└── PluginRuntimeHost 与原生设置页管理命令
```

`Nori.Desktop` 负责装配 `PluginRuntimeHost`、原生插件页面窗口，并由原生设置页经白名单调用管理命令。`Nori.Core` 不包含插件策略。

测试夹具保持独立：`Nori.PluginRuntime.Tests` 验证运行时，`Nori.PluginRuntime.TestPlugin` 是用于真实 ALC 加载的测试插件，不会进入发布包。

第三方插件必须面向 `Nori.PluginRuntime` 的公共合同编译。标准获取方式是 NuGet 上的 [`Nori.PluginSDK`](https://www.nuget.org/packages/Nori.PluginSDK/)；不要直接引用 Nori Desktop 仓库里的宿主项目，也不要复制宿主程序集。运行时实现类型均为内部类型，插件只能编译期看到明确的公共合同。旧的多个合同/运行时程序集不再发布，也不提供类型转发兼容层。

## 插件 SDK

目标框架为 .NET 10。旧版 Plugin API 2.0 对应 `Nori.PluginSDK` 2.0.0，已有动作插件仍可使用：

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

本文新增的原生页面合同属于 API 2.1。旧版 SDK 2.0.0 不包含这些接口；需要将新增公共合同同步到独立 SDK 仓库并发布对应 ref-only 包后，第三方插件才能通过 NuGet 使用页面示例。本仓库的宿主实现和测试夹具不代表 SDK 已经发布。

## 核心合同

- `INoriPlugin`：`ActivateAsync(IPluginContext, CancellationToken)` 与 `DeactivateAsync(CancellationToken)`。
- `PluginDescriptor`、`IPluginContext`、`IPluginLogger`、`IPluginStorage`、`IPluginAssets`。
- `IPluginContribution`、`IContributionRegistry` 与幂等的 `IPluginRegistration`。
- `IPluginCapability`、`IPluginCapabilities`、`PluginCapabilityStatus` 与 `[PluginCapability]`。
- `IPluginActionContribution`：活跃插件可向伴侣对话注册工具。
- `IPluginPageContribution`、`IPluginPage`：活跃插件可注册惰性创建、可释放的 Avalonia 原生页面。

插件不引用 `Nori.Core`、`Nori.Desktop`、`AppServices`、`IServiceProvider`、Bridge、窗口管理或业务运行时。`Nori.PluginRuntime` 程序集中的宿主实现由内部可见性控制，不能作为插件能力使用。

桌面宿主提供 `PluginCapabilityIds.AvaloniaUi`（`ui.avalonia`）原生页面能力。页面通过现有贡献注册表注册，不开放宿主窗口管理或通用服务容器。没有 Arcade、Games 或 Harness 公共合同；这些尚未落地的领域不会以空壳 API 进入插件边界。

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
  "capabilities": [],
  "optionalCapabilities": [],
  "platforms": ["windows", "linux", "macos"],
  "dependencies": []
}
```

`PluginManifestReader` 在创建 ALC 前检查：

- `schemaVersion` 必须为 `1`；插件 SemVer、API `major.minor` 与 schema 是独立概念。
- `id` 必须匹配 `^[a-z0-9]+(\.[a-z0-9_-]+)+$`；`runtime.kind` 当前只允许 `dotnet`。
- `runtime.assembly` 必须是 `lib/` 下的 `.dll`；`entryType` 不接受 assembly-qualified 字符串。
- 若写出 `ui.webRoot`，它必须位于 `web/` 下；宿主不会据此打开页面。列表不能重复，required/optional capability 不能交叉。
- 启动前会拒绝宿主不认识的 required capability。`ui.webview` 现在属于未知能力，插件停留在不兼容状态，不会创建 ALC。
- 依赖使用 `<id>` 加空格分隔的 `>=`、`>`、`<=`、`<`、`=` 约束，例如 `>=1.0.0 <2.0.0`。
- 重复 JSON 属性、非法路径、未知 schema 与其它解析失败都转换为带稳定 `Code` 的 `PluginException`。

宿主默认 Plugin API 为 2.1。兼容规则是 Host major 必须等于插件 major，且 Host minor 不小于插件 minor；原有 2.0 插件继续兼容。使用原生页面的插件应声明 `"apiVersion": "2.1"` 和 `"capabilities": ["ui.avalonia"]`，使旧宿主在加载前明确拒绝。1.x 插件可以被发现、展示和卸载，但会标记为 `plugin.incompatible_api`，不会创建 ALC 或执行入口。

## `.noripack` 与安装目录

`.noripack` 是 ZIP，允许的布局为：

```text
manifest.json
README.md       (可选)
LICENSE         (可选)
icon.png        (可选)
lib/            (托管入口与插件私有依赖)
web/            (公开包内文件，不作为页面宿主)
assets/         (公开资源)
locales/        (公开资源)
runtimes/       (插件私有运行时依赖)
```

宿主数据目录使用包根 `<PackageRoot>/data` 的固定插件布局：

```text
<data>/plugins/
├── cache/packages/inbox/*.noripack
├── temp/staging/<temporary>/
├── data/<pluginId>/storage.json
└── installed/<pluginId>/
    ├── current.json
    ├── 1.0.0/
    └── 1.1.0/
```

`AppStoragePaths` 不允许插件运行时回退到安装目录之外；插件资源也不会进入发布包。

安装顺序是包预检、同卷 staging、安全解压、manifest/入口/引用复校验、版本目录移动和 `current.json` 原子替换。ZIP Slip、绝对路径、`..`、控制字符、重复规范化路径、符号链接、单文件/总展开大小及压缩比均拒绝。包中不得携带 `Nori.PluginRuntime.dll` 或旧合同程序集副本。

设置页的本地安装只允许宿主 Avalonia `StorageProvider` 文件选择器产生 `.noripack` 路径。任意传入路径参数会被忽略。首次继续安装前会提示插件属于 trusted in-process 扩展；确认写入配置键 `plugins_native_trust_confirmed`。

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
  -> DeactivateAsync / cleanup
  -> 清除实例、context 与委托引用
  -> ALC.Unload + 有界 GC 检查
      -> Installed/Disabled
      -> 无法回收时 PendingRestart
```

`PluginRuntimeHost` 是唯一装配入口，拥有 `PluginManager` 和管理命令。`PluginStateStore` 保存用户启用意图以及待重启卸载请求；`plugin-startup.json` 只承担启动失败恢复。连续启动失败会保护性禁用，显式重新启用时清除该保护后重试。

激活、停用和贡献调用均在宿主边界包装。日志只记录插件 ID、版本、阶段和稳定错误码，不把参数、结果、请求正文、完整路径或 stack trace 暴露给设置页 DTO。

## 主设置页插件管理

宿主命令固定为：

```text
plugin_list
plugin_install_local
plugin_enable({ id })
plugin_disable({ id })
plugin_uninstall({ id, deleteData })
```

这些命令只允许原生设置服务的白名单调用。`plugin_list` 只做无 ALC 的 refresh/discover；安装取消返回 `{ cancelled: true }`；启用、禁用和卸载返回最新脱敏 DTO。

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

## 插件动作与包内文件

插件可以注册 `IPluginActionContribution`（Id/Description/参数 Schema/InvokeAsync）。宿主在活跃插件变化时把这些动作注册进 `ToolRegistry` 的 `plugin` 分类（工具名 `plugin__<pluginId>__<actionId>`，权限级别 safe），伴侣 AI 对话即可调用。动作在插件进程内执行，不通过页面事件通道。

`IPluginAssets` 只读取安装目录里的公开文件：`icon.png`、`web/`、`assets/`、`locales/`。`GetUri` 返回该文件的绝对 `file` URI。路径必须留在安装目录内，并拒绝符号链接跳出。宿主不提供回环 HTTP，也不将包内 Web 文件当作页面打开。

## 原生 Avalonia 页面

页面是受信任的进程内 UI，不是沙箱。桌面宿主开启 `ui.avalonia` 支持，安全模式仍不执行插件。插件必须声明该能力；未声明、不可用、重复页面 ID 或停用后注册都会被拒绝。ID 在插件内唯一，采用不超过 128 字符的 ASCII 字母、数字、`.`、`_`、`-`，标题为不超过 128 字符的非空文本。也可以把能力声明为 optional，并检查 `context.Capabilities.Statuses` 后选择是否注册页面。

公共合同：

```csharp
public interface IPluginPageContribution : IPluginContribution
{
	string Id { get; }
	string Title { get; }
	IPluginPage CreatePage();
}

public interface IPluginPage : IDisposable
{
	Avalonia.Controls.Control Control { get; }
}
```

插件在 `ActivateAsync` 中只注册工厂，不创建控件。例如：

```csharp
using Avalonia.Controls;
using Nori.PluginRuntime;

public sealed class DemoPlugin : INoriPlugin
{
	private IPluginRegistration? _registration;

	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_registration = context.Contributions.Register(new DemoPageContribution());
		return ValueTask.CompletedTask;
	}

	public ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		_registration?.Dispose();
		_registration = null;
		return ValueTask.CompletedTask;
	}
}

public sealed class DemoPageContribution : IPluginPageContribution
{
	public string Id => "overview";
	public string Title => "示例页面";
	public IPluginPage CreatePage() => new DemoPage();
}

public sealed class DemoPage : IPluginPage
{
	private readonly StackPanel _root = new()
	{
		Children = { new TextBlock { Text = "这是插件提供的原生页面。" } },
	};

	public Control Control => _root;

	public void Dispose()
	{
		// 在此停止页面任务、解除事件订阅并释放插件自有资源。
		_root.Children.Clear();
	}
}
```

用户从 **设置 → 插件** 中的页面按钮打开独立原生窗口。同一个插件页面已打开时仅激活已有窗口；关闭后释放页面，再次打开会调用工厂创建新实例。页面工厂和 `Dispose` 均在 Avalonia UI 线程执行。插件的后台任务更新控件时仍须使用 `Dispatcher.UIThread`，不得在 UI 线程执行阻塞 I/O。

每次工厂调用必须返回新的页面实例及未挂载的非窗口 `Control`。不能返回 `Window`/`TopLevel`、空控件或已经挂载到其他视图的控件。页面 ID 和标题应在注册期间保持稳定；插件负责自己内容的布局、可访问性与本地化，宿主负责窗口 chrome 和打开入口。

注册句柄撤销、插件停用或应用退出时，宿主先在 UI 线程关闭页面，解除内容并释放实例引用，再尝试 ALC 卸载。设置窗口隐藏不影响这一步。插件必须自行清理全局事件、计时器、后台任务及其他长期引用；Avalonia 的全局类型/资源缓存也可能保留插件类型，因此原生页面不保证所有插件都能热卸载，无法回收时仍显示 `pending_restart`。

插件编译依赖须与宿主 Avalonia **12.1.1** 对齐。宿主共享 `Avalonia`、`Avalonia.Base`、`Avalonia.Controls`、`Avalonia.Markup`、`Avalonia.Markup.Xaml`、`Avalonia.Themes.Fluent`，以及宿主已有的 `Avalonia.Controls.DataGrid`、`Avalonia.Controls.ColorPicker`、`Avalonia.Controls.ItemsRepeater`，以保持 `Control` 类型身份；其他 Avalonia 程序集引用会被拒绝。`.noripack` 不得携带 Avalonia 框架程序集副本（改名也不允许）。插件私有业务依赖仍保留在插件 ALC 中，`Nori.Core`、`Nori.Desktop` 和旧合同程序集引用继续禁止。`ui.webview` 仍然不受支持。

## 当前未实现

- Marketplace、联网下载/安装、签名与供应链验证、自动更新。
- WASM 与 out-of-process 插件沙箱。
- Arcade、Games、Harness runtime 及对应 WebSocket/world/patch/审批 adapter。
- WebView、向宿主固定设置导航动态注入页面、插件直接访问宿主窗口管理。
- 任意 network/filesystem/shell/process/LLM/memory/MCP/automation/pet/chat capability。
