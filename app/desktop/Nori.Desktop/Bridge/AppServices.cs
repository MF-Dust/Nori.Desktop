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

	private Account.SignInCoordinator? _signIn;

	/// <summary>
	/// 云端账户的登录协调器。
	///
	/// 走 <see cref="PublicHttp"/>：这是公网调用，不该和指向本机/内网模型服务的
	/// <see cref="Http"/> 共用一个客户端 —— 后者可能配了只在本机成立的代理与超时。
	///
	/// 同意对话框的宿主窗口在**调用时**才解析，不是装配时 —— 装配发生在窗口建好之前。
	/// </summary>
	public Account.SignInCoordinator SignIn => _signIn ??= CreateSignIn();

	/// <summary>
	/// 造登录协调器，并把「登录成功」接到界面刷新上。
	///
	/// 登录成功之后必须让快照失效：设置里的「账户与同步」页、托盘标题、WebView 那一侧，
	/// 读的都是快照的 account 段。缺这一步的症状是**登录完界面一切照旧** —— 那一页仍写着
	/// 「未登录」，备份与恢复仍是禁用的，直到别的操作碰巧让快照失效才跟上。
	///
	/// 接在协调器的事件上，不接在账户窗口的关闭回调上：登录入口不止一个（设置页、托盘、
	/// 以后可能还有别的），而这个事件是它们共同的终点。
	/// </summary>
	private Account.SignInCoordinator CreateSignIn()
	{
		Account.SignInCoordinator coordinator = Account.SignInCoordinator.Create(
			PublicHttp, Config, AskConsentAsync, Logger);
		coordinator.SignedIn += _ =>
		{
			Runtime?.InvalidateSnapshot("account");
			// 托盘那一条要从「登录…」换成账户名，它不读快照，只能单独喊一次。
			Avalonia.Threading.Dispatcher.UIThread.Post(Tray.TrayMenu.Refresh);
		};
		return coordinator;
	}

	/// <summary>
	/// 弹一次条款确认。
	///
	/// 找不到宿主窗口时返回「不同意」。这个默认值决定了在异常路径上我们是替用户签了字，
	/// 还是让他重来一次 —— 只有后者是可接受的。
	/// </summary>
	private Nori.Core.Cloud.CloudSyncService? _cloudSync;

	/// <summary>
	/// 云端存档的同步。偏好、记忆与提醒；**不含对话原文**。
	///
	/// 与 <see cref="SignIn"/> 共用同一个 <see cref="Nori.Core.Cloud.AccountSession"/>，
	/// 否则退出登录之后这一侧还会拿着旧令牌继续上传。
	///
	/// 记忆那一块**必须用 Runtime 手上那个** <c>MemoryTransferService</c>：它是带
	/// <c>queueEmbedding</c> 建起来的，另起一个会让恢复进来的记忆没有向量 —— 它们存在，
	/// 但检索不到，而且不报错。所以这个属性依赖运行时就绪。
	/// </summary>
	public Nori.Core.Cloud.CloudSyncService CloudSync
	{
		get
		{
			Runtime.AppRuntime runtime = Runtime
				?? throw new InvalidOperationException("应用运行时尚未就绪，暂时无法同步");
			return _cloudSync ??= new Nori.Core.Cloud.CloudSyncService(
				new Nori.Core.Cloud.NoriCloudClient(PublicHttp,
					Config.GetStringOr(Nori.Core.Cloud.NoriCloudClient.BaseUrlKey,
						Nori.Core.Cloud.NoriCloudClient.DefaultBaseUrl)),
				SignIn.Session,
				new Nori.Core.Cloud.CloudSaveService(
					Config, runtime.Memory.Transfer, new Nori.Core.Proactive.ReminderStore(Database)),
				Config);
		}
	}

	private static Task<bool> AskConsentAsync(Account.ConsentRequest request)
	{
		Avalonia.Controls.Window? owner =
			Avalonia.Application.Current?.ApplicationLifetime
				is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
			? desktop.Windows.FirstOrDefault(window => window.IsActive) ?? desktop.Windows.FirstOrDefault()
			: null;
		return owner is null
			? Task.FromResult(false)
			: Account.ConsentDialog.AskAsync(owner, request);
	}

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

	/// <summary>
	/// 受限执行的启动器；留空表示按平台自动挑选。
	///
	/// 这个口子是给测试的：自动挑选在 Windows 上会创建 AppContainer 配置文件，那是持久的
	/// 机器状态，测试跑完不该留下。
	/// </summary>
	public Nori.Core.Sandbox.ISandboxLauncher? Sandbox { get; init; }

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
