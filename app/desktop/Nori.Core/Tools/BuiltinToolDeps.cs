using System.Text.Json.Nodes;
using Nori.Core.Agent;

namespace Nori.Core.Tools;

/// <summary>
/// 工具授权请求回调
/// 返回 false (拒绝 / 超时 / 取消) 时 fail-closed 不执行。
/// </summary>
public delegate Task<bool> ToolApprovalHandler(ToolApprovalRequest request);

/// <summary>
/// 工具执行上下文 (随单次调用透传)
/// </summary>
public sealed class ToolContext
{
	/// <summary>所属 Agent session ID, MCP 工具据此向宿主登记取消</summary>
	public string? SessionId { get; init; }

	/// <summary>会话取消信号</summary>
	public CancellationToken CancellationToken { get; init; }

	/// <summary>逐调用授权回调; confirm/dangerous 工具缺少该回调时 fail-closed 拒绝执行</summary>
	public ToolApprovalHandler? Approve { get; init; }

	/// <summary>授权回调最长等待时间；超时按拒绝处理。</summary>
	public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// 内置工具依赖集合
///
/// 由宿主装配层注入实现, Core 侧只依赖这些窄接口保证可测试。
/// </summary>
public sealed class BuiltinToolDeps
{
	/// <summary>记忆服务</summary>
	public required Nori.Core.Memory.MemoryService Memory { get; init; }

	/// <summary>情绪管理器</summary>
	public required Nori.Core.Emotion.EmotionManager Emotion { get; init; }

	/// <summary>主动提醒调度</summary>
	public required Nori.Core.Proactive.ProactiveScheduler Proactive { get; init; }

	/// <summary>伴侣动作/表情控制 (伴侣未加载时可为 null)</summary>
	public IPetActions? Pet { get; init; }

	/// <summary>剪贴板读写</summary>
	public IClipboardOps? Clipboard { get; init; }

	/// <summary>系统信息与电池</summary>
	public required ISystemInfoProvider SystemInfo { get; init; }

	/// <summary>受限网页抓取</summary>
	public required IWebPageFetcher Fetcher { get; init; }

	/// <summary>共享 HTTP 客户端 (搜索/天气等出站请求)</summary>
	public required HttpClient Http { get; init; }

	/// <summary>配置存储 (AnySearch 端点/密钥策略读取)</summary>
	public required Nori.Core.Configuration.ConfigStore Config { get; init; }

	/// <summary>用系统默认程序打开链接 (null 时 openUrl 工具报错)</summary>
	public Action<string>? OpenUrl { get; init; }
}

/// <summary>伴侣动作/表情控制</summary>
public interface IPetActions
{
	IReadOnlyList<string> MotionNames { get; }
	IReadOnlyList<string> ExpressionNames { get; }

	/// <summary>按名称播放动作</summary>
	bool PlayMotionByName(string name);

	/// <summary>播放指定表情</summary>
	bool PlayExpression(string name);
}

/// <summary>剪贴板读写能力</summary>
public interface IClipboardOps
{
	/// <summary>读取剪贴板纯文本</summary>
	Task<string> GetTextAsync(CancellationToken cancellationToken = default);

	/// <summary>写入剪贴板纯文本</summary>
	Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>系统信息提供者</summary>
public interface ISystemInfoProvider
{
	/// <summary>获取宿主运行环境信息 (平台/语言/在线状态)</summary>
	object GetInfo();

	/// <summary>获取电池状态; 设备不支持时返回 null</summary>
	object? GetBatteryStatus();
}

/// <summary>受限网页抓取 (SSRF 防护 + 标签剥离 + 截断)</summary>
public interface IWebPageFetcher
{
	/// <summary>抓取公开网址正文摘要</summary>
	Task<object> FetchAsync(string url, CancellationToken cancellationToken = default);
}
