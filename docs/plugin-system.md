# Nori Plugin Specification 2.4

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

目标框架为 .NET 10。当前 Plugin API 2.4 的合同已同步到独立 SDK 仓库，推荐版本 2.4.1（最低 2.4.0；发布前以该仓库的 NuGet 页面为准）：

```bash
dotnet add package Nori.PluginSDK --version 2.4.1
```

或在项目文件中声明：

```xml
<ItemGroup>
  <PackageReference Include="Nori.PluginSDK" Version="2.4.1" />
</ItemGroup>
```

`Nori.PluginSDK` 是仅编译期使用的 ref-only 包。NuGet 包 ID 为 `Nori.PluginSDK`，其中合同程序集名称仍是 `Nori.PluginRuntime`；包只提供 `ref/net10.0/Nori.PluginRuntime.dll`，没有 runtime asset。插件运行时由 Nori Desktop 提供自己的 `Nori.PluginRuntime` 宿主程序集。

因此 `.noripack` 不得携带 `Nori.PluginRuntime.dll`。插件作者无需克隆 SDK 仓库或使用跨仓库 `ProjectReference`；SDK 源码、示例与更完整的作者指南位于 [`MF-Dust/Nori.PluginSDK`](https://github.com/MF-Dust/Nori.PluginSDK)。

SDK 包依赖 Avalonia 12.1.1 与 `Microsoft.Extensions.AI.Abstractions` 10.9.0，但只提供编译期资产；宿主共享自身的 Avalonia 与 AI 抽象程序集。API 与 SDK 的对应关系：

| Plugin API | 合同 | 最低 SDK |
| --- | --- | --- |
| 2.0 | 生命周期、存储、资源、贡献、能力、`IPluginActionContribution` | 2.0.0 |
| 2.1 | `IPluginPageContribution` / `IPluginPage` 与 `ui.avalonia` | 2.3.0 |
| 2.3 | `IPluginActionContribution.Permission` / `PluginActionPermission` | 2.3.0 |
| 2.4 | `IPluginChatProviderContribution` / `ai.provider`、`IPluginPageContribution.Placement`、`IPluginApplicationCapability` / `applications` | 2.4.0 |

宿主不使用 API 2.2。已发布的 SDK 2.2.0 携带宿主早已移除的 WebView 合同，不应再用于新插件；SDK 2.3.0 已删除这些类型。修改公共合同时，必须同步更新独立 SDK 仓库并发布新版本。2.4.0 增加 API 2.4 合同；2.3.0 插件仍可加载。2.4.1 合同不变，只在 XML 文档与作者指南中补充宿主注册校验规则。

## 核心合同

- `INoriPlugin`：`ActivateAsync(IPluginContext, CancellationToken)` 与 `DeactivateAsync(CancellationToken)`。
- `PluginDescriptor`、`IPluginContext`、`IPluginLogger`、`IPluginStorage`、`IPluginAssets`。
- `IPluginContribution`、`IContributionRegistry` 与幂等的 `IPluginRegistration`。
- `IPluginCapability`、`IPluginCapabilities`、`PluginCapabilityStatus` 与 `[PluginCapability]`。
- `IPluginActionContribution`：活跃插件可向伴侣对话注册工具；`Permission`（`PluginActionPermission`，API 2.3）声明调用权限级别。
- `IPluginPageContribution`、`IPluginPage`：活跃插件可注册惰性创建、可释放的 Avalonia 原生页面；`Placement`（API 2.4）选择独立窗口或设置侧栏。
- `IPluginChatProviderContribution`：`ai.provider` 能力下的聊天提供商，返回 `Microsoft.Extensions.AI.IChatClient`。
- `IPluginApplicationCapability`：`applications` 能力下的进程查询、启动与退出等待，不提供终止或外壳执行。

插件不引用 `Nori.Core`、`Nori.Desktop`、`AppServices`、`IServiceProvider`、Bridge、窗口管理或业务运行时。`Nori.PluginRuntime` 程序集中的宿主实现由内部可见性控制，不能作为插件能力使用。

桌面宿主提供 `PluginCapabilityIds.AvaloniaUi`（`ui.avalonia`）、`PluginCapabilityIds.ChatProvider`（`ai.provider`）与 `PluginCapabilityIds.Applications`（`applications`）三项能力。贡献通过现有注册表注册，不开放宿主窗口管理或通用服务容器。宿主默认 Plugin API 为 2.4。没有 Arcade、Games 或 Harness 公共合同；这些尚未落地的领域不会以空壳 API 进入插件边界。

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

宿主默认 Plugin API 为 2.4。兼容规则是 Host major 必须等于插件 major，且 Host minor 不小于插件 minor；原有 2.0/2.1 插件继续兼容。使用原生页面的插件应至少声明 `"apiVersion": "2.1"` 和 `"capabilities": ["ui.avalonia"]`；依赖动作权限审批的插件必须声明 `"apiVersion": "2.3"`，使旧宿主（会把所有动作当作 safe）在加载前明确拒绝；使用 AI 提供商、`Settings` 页面或应用互操作的插件必须声明 `"apiVersion": "2.4"`。1.x 插件可以被发现、展示和卸载，但会标记为 `plugin.incompatible_api`，不会创建 ALC 或执行入口。

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
plugin_reload({ id })
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

`plugin_reload` 在同一个生命周期串行区内停止插件、撤销贡献与页面、尝试卸载旧程序集，再从 `current.json` 重新发现并加载当前版本。保留插件数据和用户启用意图；不会隐式启用已禁用插件。仍被活动插件必需依赖使用时拒绝重载。旧程序集无法回收时返回 `pending_restart`，不会并行启动第二份实例。安全模式、待重启卸载及失败保护禁用状态不能重载。

设置页支持按名称、ID、描述或作者搜索插件、仅显示运行中的插件及手动刷新。活动插件可使用“重新加载”，包括尝试切换已安装的更新；是否成功热切换取决于旧程序集能否卸载。

Safe Mode 允许列出、禁用和卸载；拒绝本地安装、启用与重新加载。Safe Mode 不覆盖 `plugin-state.json` 中保存的用户启用意图，且不会创建 ALC、插件存储或执行第三方入口。

## 插件动作与包内文件

插件可以注册 `IPluginActionContribution`（Id/Description/参数 Schema/InvokeAsync/Permission）。宿主在活跃插件变化时把这些动作注册进 `ToolRegistry` 的 `plugin` 分类（工具名 `plugin__<pluginId>__<actionId>`），伴侣 AI 对话即可调用。动作在插件进程内执行，不通过页面事件通道。

动作注册时校验：ID 为 1–128 个 ASCII 字母、数字、`.`、`_`、`-`，在插件内按大小写精确去重；描述非空且不超过 2,000 字符，不接受换行、回车、制表符以外的控制字符；参数 Schema 必须为 JSON 对象或 `null`。撤销单个注册句柄后可以复用该 ID。最终组合工具名仍须满足宿主工具名总长限制（128 字符），因此动作 ID 应为插件 ID 留出空间。动作 ID、描述与 Schema 应在注册期间保持稳定。

设置 → 插件的活动插件卡片可展开“插件动作”，查看宿主实际注册的工具名称、描述、权限和启用状态。动作开关复用宿主工具启用配置，不停用插件及其页面。手动测试展示参数 Schema 并接受 JSON 对象，只允许已启用的 `safe` 动作；`confirm` / `dangerous` 动作继续通过对话的现有审批流程执行，不提供绕过审批的测试入口。

`Permission` 按以下规则映射为工具权限级别；未实现该成员的旧插件使用默认值 `Safe`，读取时抛出异常或返回未知值则按 `dangerous` 处理：

| `PluginActionPermission` | 工具权限 | 行为 |
| --- | --- | --- |
| `Safe` | safe | 直接执行 |
| `Confirm` | confirm | 逐次请求用户授权，用户的权限档位可放行 |
| `Dangerous` | dangerous | 除完全放行档外每次都请求用户授权 |

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

页面默认（`PluginPagePlacement.Window`）从 **设置 → 插件** 中的页面按钮打开独立原生窗口。同一个插件页面已打开时仅激活已有窗口；关闭后释放页面，再次打开会调用工厂创建新实例。

API 2.4 可重写 `Placement => PluginPagePlacement.Settings`，让页面直接作为 **设置 → 插件设置** 分组中的导航项。宿主在页面首次进入时调用工厂并挂载控件，切走不一定立即释放；插件停用、贡献撤销或应用退出时同步卸下控件并调用 `Dispose`，即使设置窗口当时隐藏。同一个插件不能同时注册同 ID 的两种位置。设置内容区域由插件负责滚动与自适应，内嵌页不复用独立窗口 chrome。

页面工厂和 `Dispose` 均在 Avalonia UI 线程执行。插件的后台任务更新控件时仍须使用 `Dispatcher.UIThread`，不得在 UI 线程执行阻塞 I/O。

```csharp
public sealed class DemoPageContribution : IPluginPageContribution
{
	public string Id => "settings";
	public string Title => "示例设置";
	public PluginPagePlacement Placement => PluginPagePlacement.Settings;
	public IPluginPage CreatePage() => new DemoPage();
}
```

使用 `Settings` 位置的插件必须声明 `"apiVersion": "2.4"`；旧宿主不认识该成员，只在新的 2.4 宿主中显示内嵌页。

每次工厂调用必须返回新的页面实例及未挂载的非窗口 `Control`。不能返回 `Window`/`TopLevel`、空控件或已经挂载到其他视图的控件。页面 ID 和标题应在注册期间保持稳定；插件负责自己内容的布局、可访问性与本地化，宿主负责窗口 chrome 和打开入口。

注册句柄撤销、插件停用或应用退出时，宿主先在 UI 线程关闭页面，解除内容并释放实例引用，再尝试 ALC 卸载。设置窗口隐藏不影响这一步。插件必须自行清理全局事件、计时器、后台任务及其他长期引用；Avalonia 的全局类型/资源缓存也可能保留插件类型，因此原生页面不保证所有插件都能热卸载，无法回收时仍显示 `pending_restart`。

插件编译依赖须与宿主 Avalonia **12.1.1** 对齐。宿主共享 `Avalonia`、`Avalonia.Base`、`Avalonia.Controls`、`Avalonia.Markup`、`Avalonia.Markup.Xaml`、`Avalonia.Themes.Fluent`，以及宿主已有的 `Avalonia.Controls.DataGrid`、`Avalonia.Controls.ColorPicker`、`Avalonia.Controls.ItemsRepeater`，以保持 `Control` 类型身份；其他 Avalonia 程序集引用会被拒绝。`.noripack` 不得携带 Avalonia 框架程序集副本（改名也不允许）。插件私有业务依赖仍保留在插件 ALC 中，`Nori.Core`、`Nori.Desktop` 和旧合同程序集引用继续禁止。`ui.webview` 仍然不受支持。

## 插件 AI 提供商

声明 `ai.provider` 的插件可实现 `IPluginChatProviderContribution`。宿主把每个贡献注册为稳定 ID `plugin:<pluginId>:<providerId>`，并只在用户选择它时把当前配置的地址、密钥与模型交给插件。`IChatClient` 来自 `Microsoft.Extensions.AI`，因此文本、图片、流式输出、用量与工具调用沿用宿主现有转换，不需要插件引用 `Nori.Core`。

```csharp
public interface IPluginChatProviderContribution : IPluginContribution
{
	string Id { get; }
	string Name { get; }
	string DefaultBaseUrl => "";
	bool RequiresApiKey => true;
	Microsoft.Extensions.AI.IChatClient CreateClient(PluginChatClientOptions options);
	ValueTask<IReadOnlyList<string>> GetModelsAsync(PluginChatClientOptions options, CancellationToken cancellationToken);
}
```

约定：

- `Id` 为插件内唯一的 1–64 字符 ASCII 字母、数字、`_`、`-`；`Name` 非空且不超过 128 字符。`DefaultBaseUrl` 为空允许本地客户端，否则必须是绝对 `http`/`https` 地址且不含用户信息。未注册能力、ID 重复或元数据无效时注册失败。
- `CreateClient` 每次返回新的独立客户端，由宿主在请求或停用后释放；不要返回进程级共享单例。`GetModelsAsync` 接收空 `Model`，返回的 ID 由宿主去除空白、控制字符并去重，最多 1000 个。
- 配置中的提供商 ID 原样保存；插件被撤销或未启用时不回退到 OpenAI，而是明确报“插件聊天提供商不可用”。模型选择、连接探测、桌面视觉、伴侣互动与记忆反思都走同一注册表。
- 宿主的思考档位与 models.dev 模型能力不作用于插件提供商：`PluginChatClientOptions` 只含地址、密钥与模型，上下文窗口按保守默认值计算；需要思考参数的插件应在自身客户端内处理。
- 提供商只在宿主正常模式下注册；安全模式不激活插件，也不注册提供商。插件客户端必须遵守传入的取消令牌，宿主在撤销时取消在途请求。
- Ai provider 插件必须声明 `"apiVersion": "2.4"` 与 `"capabilities": ["ai.provider"]`。`.noripack` 不得携带 `Microsoft.Extensions.AI.Abstractions.dll`、Avalonia 或合同程序集；宿主共享 AI 抽象类型。

## 应用互操作

声明 `applications` 的插件可从 `context.Capabilities.GetRequired<IPluginApplicationCapability>()` 取得能力：

```csharp
Task<IReadOnlyList<PluginApplicationInfo>> GetRunningAsync(CancellationToken cancellationToken = default);
Task<int> LaunchAsync(PluginApplicationLaunchRequest request, CancellationToken cancellationToken = default);
Task WaitForExitAsync(int processId, CancellationToken cancellationToken = default);
```

- 查询只返回进程 ID 与名称，不返回路径、命令行或环境变量。
- 启动要求已存在的可执行文件绝对路径，`UseShellExecute=false`，参数按独立项传递，不经过命令解释器；Windows 上拒绝 `.bat` / `.cmd`（系统会隐式交给 cmd.exe 解析参数）。可选工作目录也必须是已存在的绝对路径。
- 宿主不跟踪也不终止插件启动的进程；撤销、取消或应用退出只取消等待。等待已退出的进程立即完成。
- 插件必须声明 `"apiVersion": "2.4"` 与 `"capabilities": ["applications"]`，并在 README 中说明其启动的外部程序。该能力不做进程沙箱，仍属受信任的进程内扩展。

## 当前未实现

- Marketplace、联网下载/安装、签名与供应链验证、自动更新。
- WASM 与 out-of-process 插件沙箱。
- Arcade、Games、Harness runtime 及对应 WebSocket/world/patch/审批 adapter。
- WebView、插件直接访问宿主窗口管理或任意导航注入（只支持已声明的 `Settings` 页面）。
- 任意 network/filesystem/shell/LLM/memory/MCP/automation/pet/chat capability；AI 提供商与进程互操作只通过上文两项受限能力提供。

## 官方插件

- [`MF-Dust/Nori.Plugins.Browser`](https://github.com/MF-Dust/Nori.Plugins.Browser)（`io.nori.browser`）：原宿主内置的 Playwright/Edge 浏览器 DOM 自动化。宿主不再引用 Playwright，也不再提供 `automation_browser_*` 命令和设置开关；插件以 `.noripack` 单独发布，自带 win32_x64 Playwright 驱动，通过 `run`（confirm）、`run_with_input`（dangerous）等动作和 `ui.avalonia` 控制页面提供能力。
