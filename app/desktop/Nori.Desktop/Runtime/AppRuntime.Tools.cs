using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Mcp;
using Nori.Core.Network;
using Nori.Core.Sandbox;
using Nori.Core.Security;
using Nori.Core.Tools;
using Nori.Core.Vision;
using Nori.Core.Expression;
using Nori.Desktop.Expression;
using Nori.Desktop.Vision;
using Nori.PluginRuntime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	/// <summary>
	/// 同步已连接 MCP 工具到 Agent 注册表。
	/// 每个动态工具默认 confirm, 由 AgentRuntime 的逐调用授权链路 fail-closed 控制。
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "刷新失败日志需要保留最后一个服务标识，后台结果按生命周期受控处理。")]
	public async Task RefreshMcpToolsAsync(CancellationToken cancellationToken = default)
	{
		// 安全模式不能通过聊天启动或其他间接路径刷新外部 MCP。
		if (Services.SafeMode) return;

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
		CancellationToken ct = linkedCts.Token;
		bool entered = false;
		string failureServerId = "unknown";
		try
		{
			// 串行化刷新, 防止较早的慢刷新在较新的结果之后覆盖工具集合。
			await _mcpRefreshGate.WaitAsync(ct).ConfigureAwait(false);
			entered = true;
			ct.ThrowIfCancellationRequested();

			// 所有连接状态、Schema 和工具闭包都先在局部集合中完成。
			// 任何失败或取消都不能触碰注册表中的上一版工具。
			IReadOnlyList<McpServerStatusInfo> servers = await Services.Mcp.GetServersAsync().ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();

			McpServerStatusInfo? unavailable = servers.FirstOrDefault(server =>
				string.Equals(server.Status, "error", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(server.Status, "connecting", StringComparison.OrdinalIgnoreCase)
				|| (!string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(server.Status, "disconnected", StringComparison.OrdinalIgnoreCase)));
			if (unavailable is not null)
			{
				LogMcpRefreshFailure(
					unavailable.ServerId,
					string.Equals(unavailable.Status, "error", StringComparison.OrdinalIgnoreCase)
						? "server-error"
						: "server-not-ready");
				return;
			}

			List<RegisteredTool> replacements = [];
			HashSet<string> replacementNames = new(StringComparer.Ordinal);
			foreach (McpServerStatusInfo server in servers.Where(server =>
				string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase)))
			{
				failureServerId = server.ServerId;
				foreach (McpToolDefinition definition in server.Tools)
				{
					ct.ThrowIfCancellationRequested();
					string serverId = server.ServerId;
					string toolName = definition.Name;
					if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(toolName))
						throw new InvalidOperationException("MCP 工具定义无效");

					string fullName = $"mcp__{serverId}__{toolName}";
					if (!replacementNames.Add(fullName))
						throw new InvalidOperationException("MCP 工具名称重复");

					JsonObject schema = ToolLimits.CapSchema(definition.InputSchema);
					replacements.Add(new RegisteredTool
					{
						Name = fullName,
						Description = $"[{server.Name}] {McpConfigValidator.CapDescription(definition.Description ?? toolName)}",
						Parameters = schema,
						PermissionLevel = "confirm",
						Category = McpToolCategory,
						Execute = async (arguments, context) =>
						{
							JsonObject? objectArguments = arguments as JsonObject;
							McpToolResult result = await Services.Mcp.CallToolAsync(serverId, toolName, objectArguments, context.CancellationToken);
							if (result.IsError) throw new InvalidOperationException(result.AsText());
							return result.AsText();
						},
					});
				}
			}

			ct.ThrowIfCancellationRequested();
			failureServerId = "unknown";
			Tools.ReplaceCategory(McpToolCategory, replacements);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			LogMcpRefreshFailure(failureServerId, "cancelled");
			throw;
		}
		catch (Exception exception)
		{
			// 只记录服务 ID 和固定类别, 不写入异常正文、Schema、参数或工具结果。
			LogMcpRefreshFailure(failureServerId, McpRefreshErrorCategory(exception));
		}
		finally
		{
			if (entered) _mcpRefreshGate.Release();
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "诊断写入失败不能覆盖已分类的 MCP 刷新失败。")]
	private void LogMcpRefreshFailure(string? serverId, string category)
	{
		string safeServerId = CapMcpLogPart(serverId, McpRefreshLogServerIdMaxCharacters);
		string safeCategory = CapMcpLogPart(category, 32);
		string message = $"MCP 工具刷新失败: server_id={safeServerId} category={safeCategory}";
		if (message.Length > McpRefreshLogMaxCharacters) message = message[..McpRefreshLogMaxCharacters];
		try { Services.Logger.Write(LogSource.Backend, "warn", message); }
		catch { }
	}

	private static string McpRefreshErrorCategory(Exception exception) => exception switch
	{
		OperationCanceledException => "cancelled",
		TimeoutException => "timeout",
		JsonException => "schema",
		IOException => "transport",
		ObjectDisposedException => "lifecycle",
		InvalidOperationException => "definition",
		_ => "refresh",
	};

	private static string CapMcpLogPart(string? value, int maxCharacters)
	{
		if (string.IsNullOrEmpty(value) || maxCharacters <= 0) return "unknown";
		return value.Length <= maxCharacters ? value : value[..maxCharacters];
	}

	/// <summary>活跃插件集合变化后防抖刷新插件工具。</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "计时器回调按生命周期触发后台刷新，任务结果由观察逻辑处理。")]
	private void SchedulePluginToolsRefresh()
	{
		_pluginToolsRefreshTimer?.Dispose();
		_pluginToolsRefreshTimer = new Timer(
			timerState => { _ = RefreshPluginToolsAsync(); },
			null, PluginToolsRefreshDebounceMs, Timeout.Infinite);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "插件工具刷新失败需记录并继续运行主界面。")]
	private async Task RefreshPluginToolsAsync()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		PluginRuntimeHost? pluginRuntime = Services.PluginRuntime;
		if (pluginRuntime is null) return;
		bool entered = false;
		try
		{
			if (!await _pluginToolsRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
			entered = true;

			List<RegisteredTool> tools = [];
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach ((PluginDescriptor plugin, IPluginActionContribution action) in pluginRuntime.GetContributionsWithSource<IPluginActionContribution>())
			{
				if (string.IsNullOrWhiteSpace(action.Id)) continue;
				string pluginName = plugin.Id.Replace('.', '_');
				string fullName = $"plugin__{pluginName}__{action.Id}";
				if (!names.Add(fullName)) continue;
				tools.Add(new RegisteredTool
				{
					Name = fullName,
					Description = $"[{plugin.Name}] {action.Description}",
					Parameters = ToolLimits.CapSchema(action.ParametersSchema as JsonObject ?? new JsonObject()),
					PermissionLevel = "safe",
					Category = PluginToolCategory,
					Execute = async (arguments, context) =>
						await action.InvokeAsync(arguments, context.CancellationToken).ConfigureAwait(false),
				});
			}
			Tools.ReplaceCategory(PluginToolCategory, tools);
		}
		catch (Exception exception)
		{
			// 只记录类别与摘要, 不写入插件参数或结果
			try { Services.Logger.Write(LogSource.Backend, "warn", $"插件工具刷新失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); }
			catch { }
		}
		finally
		{
			if (entered) _pluginToolsRefreshGate.Release();
		}
	}

	/// <summary>
	/// 改完工作目录之后重建内建工具。
	///
	/// 文件工具在注册时将工作目录捕获进闭包，不重建则配置变更要到下次启动才生效，表现为保存
	/// 未成功。MCP 与插件工具按各自分类原子替换，本方法不涉及。
	/// </summary>
	public void RebuildTools()
	{
		WorkspaceAccess workspace = ResolveWorkspace();
		WorkspaceTools.RegisterAll(Tools, workspace);
		RegisterTaskTools(Tools, workspace);
		RegisterScreenTools(Tools);
		DeviceTools.RegisterAll(Tools, RefreshDevices);
	}

	/// <summary>
	/// 注册读屏工具。用户未显式开启、或处于安全模式时不注册。
	///
	/// 授权与工作目录分开：文件访问的范围是用户挑的一个文件夹，屏幕上会出现什么他在授权那一刻
	/// 无从预料。能力是否具备（平台支持、模型已配）由 ScreenTools 自己判断。
	/// </summary>
	private void RegisterScreenTools(ToolRegistry registry)
	{
		bool allowed = !Services.SafeMode
			&& Services.Config.GetBoolOr(ConfigStore.KeyScreenReadingEnabled, false);
		ScreenTools.RegisterAll(registry, allowed ? ScreenCapture : null, allowed ? VisionAnalyzer : null);
	}

	/// <summary>读屏实现；非 Windows 暂无实现，返回 null 即整组不注册。</summary>
	private IScreenCapture? ScreenCapture =>
		OperatingSystem.IsWindows() ? _screenCapture ??= new WindowsScreenCapture() : null;

	/// <summary>截图分析器，走当前聊天 Provider 的多模态能力。</summary>
	private IVisionAnalyzer VisionAnalyzer =>
		_visionAnalyzer ??= new ChatVisionAnalyzer(Services.Chat, Services.AiSettings);

	/// <summary>
	/// 注册 runTask。没有任务时直接注销并返回，**不触碰 <see cref="Sandbox"/>**。
	///
	/// 建立启动器在 Windows 上会创建 AppContainer 配置文件，那是持久的机器状态。没配任务的
	/// 用户不该平白多出这份东西。
	/// </summary>
	private void RegisterTaskTools(ToolRegistry registry, WorkspaceAccess workspace)
	{
		IReadOnlyList<WorkspaceTask> tasks = ResolveTasks();
		if (!workspace.IsConfigured || tasks.Count == 0)
		{
			registry.Unregister(TaskTools.RunTaskName);
			return;
		}

		TaskTools.RegisterAll(registry, workspace, tasks, Sandbox);
	}

	/// <summary>
	/// 受限执行的启动器，首次使用时建立。
	///
	/// 延迟到首次使用：Windows 上建立它会创建 AppContainer 配置文件，没配任务的用户不该
	/// 平白多出这份状态。
	/// </summary>
	private ISandboxLauncher Sandbox => _sandbox ??= Services.Sandbox ?? SandboxLauncherFactory.Create();

	/// <summary>
	/// 情绪表达的通道清单。
	///
	/// 与协调器分开持有，因为默认开关值要按通道的侵入等级取 —— 若经协调器去查，
	/// 构造协调器时又要用到开关判定，就成了自引用。
	/// </summary>
	private IReadOnlyList<IExpressionChannel> ExpressionChannels => _expressionChannels ??=
	[
		new TrayIconChannel(() => Tray.TrayMenu.Current, RunOnUi),
		new SpeechBorderChannel(() => Services.Windows.Pet?.SpeechOverlay, RunOnUi),
		RgbChannel,
		AmbientChannel,
		AccentChannel,
	];

	/// <summary>灯效通道。设备探测与重连由它自己管。</summary>
	private RgbLightingChannel RgbChannel => _rgbChannel ??= new RgbLightingChannel();

	/// <summary>环境音通道。这一版只有壳：没有素材时恒为不可用。</summary>
	private AmbientSoundChannel AmbientChannel => _ambientChannel ??=
		new AmbientSoundChannel(Path.Combine(Services.Paths.DataRoot, "soundscapes"));

	/// <summary>改持久系统设置前的原值备份。</summary>
	private DesktopStateBackup DesktopBackup => _desktopBackup ??= new DesktopStateBackup(Services.Config);

	private IDesktopAppearance Appearance => _appearance ??=
		OperatingSystem.IsWindows() ? new WindowsDesktopAppearance() : new UnsupportedDesktopAppearance();

	private AccentColorChannel AccentChannel => _accentChannel ??= new AccentColorChannel(Appearance, DesktopBackup);

	/// <summary>
	/// 把改过的桌面设置还回去。
	///
	/// 三个时机都要调：关掉某条通道、退出应用、以及**启动时** —— 上一次若是崩溃或被强制结束，
	/// 桌面会停在她改过的样子，而原值存在配置库里，下次启动仍然还得回来。
	/// </summary>
	/// <summary>丢掉设备探测缓存，下次访问时重新探测。用户刚开 OpenRGB、刚插新外设时用。</summary>
	public IReadOnlyList<string> RefreshDevices()
	{
		RgbChannel.Invalidate();
		return [.. RgbChannel.Devices.Select(device => device.Name)];
	}

	public void RestoreDesktopState()
	{
		foreach (Action restore in new Action[] {AccentChannel.Restore})
		{
			try
			{
				restore();
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException
				or UnauthorizedAccessException)
			{
				Services.Logger.Write(LogSource.Backend, "warn", $"还原桌面设置失败: {exception.GetType().Name}");
			}
		}
	}

	/// <summary>情绪表达的扇出协调器。通道自己判断可用性，协调器只负责过滤与节流。</summary>
	private ExpressionCoordinator Expression => _expression ??= new ExpressionCoordinator(
		ExpressionChannels,
		IsExpressionChannelEnabled,
		(key, exception) =>
			Services.Logger.Write(LogSource.Backend, "warn", $"情绪表达通道失败 [{key}]: {exception.GetType().Name}"));

	/// <summary>
	/// 某条表达通道开没开。
	///
	/// 缺省值按侵入等级取：Global 档（系统强调色）默认关，其余默认开 —— 用户没表过态时
	/// 不该被改掉整个桌面的颜色。
	/// </summary>
	private bool IsExpressionChannelEnabled(string key) =>
		Services.Config.GetBoolOr(
			key,
			ExpressionChannels.FirstOrDefault(channel => channel.Key == key)?.Level != Intrusiveness.Global);

	private static void RunOnUi(Action action) => Dispatcher.UIThread.Post(action);

	/// <summary>
	/// 已经可用的启动器，**不触发创建**：已建好的优先，其次是注入的，都没有则为空。
	///
	/// 报告隔离强度与释放授权都不该把容器建出来，但都必须尊重注入 —— 这条规则写在一处，
	/// 两边共用。分开写过一次，结果是两处各漏了一次注入。
	/// </summary>
	private ISandboxLauncher? ExistingSandbox => _sandbox ?? Services.Sandbox;

	/// <summary>当前该给 runTask 哪些任务。安全模式下一条都不给，判据与文件工具一致。</summary>
	/// <summary>
	/// 当前授予过持久权限的路径集合：工作目录，加上各条任务的可执行文件所在目录。
	///
	/// 与 <see cref="RegisterTaskTools"/> 用的是同一套推导，两处必须一致 —— 授权面算少了会残留，
	/// 算多了会去动没授权过的目录的 ACL。
	/// </summary>
	public IReadOnlyList<string> CurrentGrantPaths()
	{
		WorkspaceAccess workspace = ResolveWorkspace();
		if (!workspace.IsConfigured) return [];

		List<string> paths = [workspace.Root];
		foreach (WorkspaceTask task in ResolveTasks())
		{
			paths.AddRange(TaskTools.ExecutableDirectories(task.Command));
		}

		return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// 释放已经不再需要的持久授权。
	///
	/// Windows 上授权写进文件系统 ACL，不随进程结束消失。工作目录换掉、任务删掉之后不释放，
	/// ACE 就永远留在用户的目录上 —— 用户看不见，也无从清理。
	///
	/// 只释放「旧的减新的」：仍在用的路径撤了还得立刻加回来，反复增删 ACE 只会放大出错面。
	/// 调用方需要在改配置**之前**取一次 <see cref="CurrentGrantPaths"/> 作为 previous。
	/// </summary>
	public void ReleaseStaleGrants(IReadOnlyList<string> previous)
	{
		ArgumentNullException.ThrowIfNull(previous);
		if (previous.Count == 0) return;

		HashSet<string> keep = new(CurrentGrantPaths(), StringComparer.OrdinalIgnoreCase);
		string[] stale = [.. previous.Where(path => !keep.Contains(path))];
		if (stale.Length == 0) return;

		// 不走 Sandbox 属性：它会顺手把容器建出来，清理路径上是反效果。
		ISandboxLauncher launcher = ExistingSandbox ?? SandboxLauncherFactory.CreateForRelease();
		foreach (string path in stale)
		{
			try
			{
				launcher.Release(new SandboxPolicy { WorkspaceRoot = path });
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
			{
				// 目录已被删除或权限不足；残留一条 ACE 不影响功能，记录即可。
				Services.Logger.Write(LogSource.Backend, "warn", $"释放沙箱授权失败 [{path}]: {exception.GetType().Name}");
			}
		}
	}

	private IReadOnlyList<WorkspaceTask> ResolveTasks() =>
		Services.SafeMode
			? []
			: WorkspaceTaskList.Read(Services.Config.Get(ConfigStore.KeyWorkspaceTasks));

	/// <summary>
	/// 当前该给文件工具哪个工作目录。构建与重建共用这一处判据。
	///
	/// 安全模式返回未配置：该组工具虽不产生网络请求，但具备对宿主文件系统的读写能力。只在
	/// 构建时判、不在重建时判的话，安全模式下改一次设置就会把工具注册回来。
	/// </summary>
	private WorkspaceAccess ResolveWorkspace() =>
		Services.SafeMode
			? new WorkspaceAccess("")
			: new WorkspaceAccess(Services.Config.GetStringOr(ConfigStore.KeyWorkspaceRoot, ""));

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "工具失败诊断写入失败不能覆盖原始工具错误。")]
	private ToolRegistry BuildToolRegistry(bool audioAvailable)
	{
		ToolRegistry registry = new()
		{
			FailureDiagnostic = (tool, diagnostic) =>
			{
				try { Services.Logger.Write(LogSource.Backend, "warn", $"工具调用失败: {diagnostic.ToLogMessage(tool)}"); }
				catch { }
			},
		};
		BuiltinTools.RegisterAll(registry, new BuiltinToolDeps
		{
			Memory = Memory,
			Emotion = Emotion,
			Proactive = Proactive,
			Pet = new PetActionsAdapter(() => Services.PetRuntime),
			Clipboard = audioAvailable ? new AvaloniaClipboardOps(() => Services.Windows.Get(WindowLabels.Main)) : null,
			SystemInfo = new DesktopSystemInfo(Services.Config),
			Fetcher = new WebPageFetcher(Services.PublicHttp),
			Http = Services.PublicHttp,
			Config = Services.Config,
			OpenUrl = url => ShellOpen.OpenUrl(url),
		});

		// 文件工具仅在配置了工作目录时注册。安全模式下一并跳过：该组工具虽不产生网络请求，
		// 但具备对宿主文件系统的读写能力，属于安全模式要禁用的范围。判据与 RebuildTools 共用。
		WorkspaceAccess workspace = ResolveWorkspace();
		WorkspaceTools.RegisterAll(registry, workspace);
		RegisterTaskTools(registry, workspace);
		RegisterScreenTools(registry);
		DeviceTools.RegisterAll(registry, RefreshDevices);
		return registry;
	}

	private IReadOnlyList<string> FlattenMotionNames()
	{
		IReadOnlyList<Core.Live2D.MotionGroupInfo>? groups = Services.PetRuntime?.MotionGroups;
		if (groups is null || groups.Count == 0) return [];
		return groups.SelectMany(group => group.Names).Distinct().ToList();
	}
}
