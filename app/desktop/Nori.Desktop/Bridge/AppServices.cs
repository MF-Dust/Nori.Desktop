using Nori.Core.Assets;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Core.Telemetry;
using Nori.Desktop.Automation;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Live2D;
using Nori.Desktop.Windows;
using Nori.PluginRuntime;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 应用级服务容器
///
/// 承接原来 Rust 侧 tauri::State 的角色: 把数据库/配置/资源/聊天/日志/窗口
/// 装配在一起交给桥接命令使用.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
	/// <summary>应用包内不可变存储路径。</summary>
	public required AppStoragePaths Paths { get; init; }

	/// <summary>数据库</summary>
	public required NoriDatabase Database { get; init; }

	/// <summary>配置读写</summary>
	public required ConfigStore Config { get; init; }

	/// <summary>统一 AI Provider 配置领域服务</summary>
	public AiSettingsStore AiSettings { get; init; } = null!;

	/// <summary>日志</summary>
	public required FileLogger Logger { get; init; }

	/// <summary>错误与性能遥测; 未装配时为空实现</summary>
	public ITelemetry Telemetry { get; set; } = NoopTelemetry.Instance;

	/// <summary>资源管理</summary>
	public required ResourceManager Resources { get; init; }

	/// <summary>聊天</summary>
	public required ChatService Chat { get; init; }

	/// <summary>记忆存储</summary>
	public required Nori.Core.Memory.MemoryStore Memory { get; init; }

	/// <summary>Embedding 向量接口 (支持 BGE-M3 / OpenAI 规范)</summary>
	public required Nori.Core.Embedding.OpenAiEmbeddingAdapter Embedding { get; init; }

	/// <summary>LLM 接口</summary>
	public required LlmClient Llm { get; init; }

	/// <summary>MCP (Model Context Protocol) 管理器</summary>
	public required Nori.Core.Mcp.McpManager Mcp { get; init; }

	/// <summary>回环资源服务</summary>
	public AssetServer? Assets { get; init; }

	/// <summary>统一插件运行时；安全模式下仅发现并标记禁用插件。</summary>
	internal PluginRuntimeHost? PluginRuntime { get; set; }

	/// <summary>自动更新服务</summary>
	public Nori.Core.Update.UpdateService? Update { get; set; }

	/// <summary>本地/模型 HTTP 客户端 (测试可在装配后替换)</summary>
	public HttpClient Http { get; set; } = null!;

	private HttpClient? _publicHttp;

	/// <summary>公网 HTTP 客户端; 未显式装配时回退到 Http 以兼容测试装配。</summary>
	public HttpClient PublicHttp
	{
		get => _publicHttp ?? Http;
		set => _publicHttp = value;
	}

	/// <summary>Agent 聊天/MCP 操作取消注册表</summary>
	public required Bridge.AgentOperationRegistry AgentOperations { get; init; }

	/// <summary>自动化宿主运行时；安全模式下仍装配但所有执行入口 fail-closed。</summary>
	public AutomationRuntime? Automation { get; set; }

	private AutomationAuditRepository? _automationAudit;

	/// <summary>自动化审计仓储；只保存固定分类和稳定失败码。</summary>
	public AutomationAuditRepository AutomationAudit => _automationAudit ??= new AutomationAuditRepository(Database);

	/// <summary>浏览器运行器工厂；生产默认使用隔离 Edge，测试可注入 fake。</summary>
	public Func<IAutomationBrowserRunner>? AutomationBrowserRunnerFactory { get; set; }

	/// <summary>桌面视觉运行器工厂；安全模式装配时必须保持为空。</summary>
	public Func<DesktopVisionRunnerRequest, IAutomationTaskRunner>? AutomationDesktopVisionRunnerFactory { get; set; }

	/// <summary>当前聊天 Provider 的桌面视觉规划器工厂；不得复制另一套 AI adapter。</summary>
	public Func<IDesktopVisionPlanner>? AutomationDesktopVisionPlannerFactory { get; set; }

	/// <summary>桌面输入动作工厂；测试可注入 fake。</summary>
	public Func<IDesktopVisionActionExecutor>? AutomationDesktopVisionActionFactory { get; set; }

	/// <summary>桌面截图工厂；测试可注入内存 fake。</summary>
	public Func<IDesktopVisionScreenshotSource>? AutomationDesktopVisionScreenshotFactory { get; set; }

	/// <summary>桌面窗口枚举工厂；测试可注入脱敏窗口 fake。</summary>
	public Func<IDesktopVisionWindowCatalog>? AutomationDesktopVisionWindowCatalogFactory { get; set; }

	/// <summary>桌面视觉审批回调；未装配时高风险动作必须拒绝。</summary>
	public DesktopVisionApprovalCallback? AutomationDesktopVisionApprovalCallback { get; set; }

	/// <summary>有界的 Agent 性能 Trace；只保存阶段/用量元数据，不保存正文。</summary>
	public AgentTraceCollector AgentTrace { get; } = new();

	/// <summary>窗口调度, 窗口建好后回填</summary>
	public IWindowManager Windows { get; set; } = null!;

	/// <summary>桥接命令, 服务装配完成后回填</summary>
	public BridgeCommands Commands { get; set; } = null!;

	/// <summary>桥接内核, 服务装配完成后回填</summary>
	public NoriBridge? Bridge { get; set; }

	/// <summary>原生 Live2D 伴侣运行时</summary>
	public PetRuntime PetRuntime { get; set; } = null!;

	/// <summary>应用业务运行时 (Agent/技能/情绪/提醒/语音), 窗口建好后回填</summary>
	public Runtime.AppRuntime? Runtime { get; set; }

	/// <summary>应用级取消令牌, 启动退出时取消并传给桥接请求。</summary>
	public CancellationToken ShutdownToken { get; set; }

	/// <summary>是否以手动安全模式启动。</summary>
	public bool SafeMode { get; init; }

	private int _disposed;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		// 先停止桥接，再并行取消彼此独立的后台子系统；单个挂起项不能挡住数据库与遥测释放。
		await DisposeStep(() => Bridge?.DisposeAsync() ?? ValueTask.CompletedTask, TimeSpan.FromSeconds(1));
		await Task.WhenAll(
			DisposeStep(() => Runtime?.DisposeAsync() ?? ValueTask.CompletedTask, TimeSpan.FromSeconds(4)),
			DisposeStep(() => Automation?.DisposeAsync() ?? ValueTask.CompletedTask, TimeSpan.FromSeconds(4)),
			DisposeStep(() => PluginRuntime?.DisposeAsync() ?? ValueTask.CompletedTask, TimeSpan.FromSeconds(4)),
			DisposeStep(() =>
			{
				Update?.Dispose();
				return ValueTask.CompletedTask;
			}, TimeSpan.FromSeconds(1)),
			DisposeStep(() => Mcp.DisposeAsync(), TimeSpan.FromSeconds(4)),
			DisposeStep(() => Assets?.DisposeAsync() ?? ValueTask.CompletedTask, TimeSpan.FromSeconds(4)));
		await DisposeStep(() =>
		{
			if (_publicHttp is not null && !ReferenceEquals(_publicHttp, Http)) _publicHttp.Dispose();
			Http.Dispose();
			return ValueTask.CompletedTask;
		}, TimeSpan.FromSeconds(1));
		await DisposeStep(() =>
		{
			Database.Dispose();
			return ValueTask.CompletedTask;
		}, TimeSpan.FromSeconds(1));
		await DisposeStep(async () => await Telemetry.FlushAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false), TimeSpan.FromSeconds(2));
		await DisposeStep(() =>
		{
			Telemetry.Dispose();
			return ValueTask.CompletedTask;
		}, TimeSpan.FromSeconds(1));
	}

	private static async Task DisposeStep(Func<ValueTask> dispose, TimeSpan timeout)
	{
		try { await dispose().AsTask().WaitAsync(timeout).ConfigureAwait(false); }
		catch { /* 继续释放彼此独立的资源。 */ }
	}
}
