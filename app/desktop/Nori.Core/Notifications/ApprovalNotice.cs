namespace Nori.Core.Notifications;

/// <summary>
/// 一条待决授权在系统通知里需要的全部信息。
///
/// 刻意不带 <c>IBridgeSource</c>、不带 CancellationToken：通知这层只负责把
/// 「她想用哪个工具」摆到你面前，回收仍然走 AppRuntime 那一份 PendingApproval。
/// 两条路解同一个 TaskCompletionSource，谁先到算谁的。
/// </summary>
public sealed record ApprovalNotice
{
	/// <summary>与应用内授权卡片同一个 id；通知按钮把它原样带回来。</summary>
	public required string RequestId { get; init; }

	/// <summary>工具名，例如 writeFile。</summary>
	public required string ToolName { get; init; }

	/// <summary>工具自己的说明，没有就为空。</summary>
	public string? Description { get; init; }

	/// <summary>safe / confirm / dangerous。</summary>
	public string PermissionLevel { get; init; } = "confirm";

	/// <summary>参数摘要，已经压成一行。</summary>
	public string? ArgumentSummary { get; init; }
}

/// <summary>
/// 系统通知的呈现层。
///
/// **应用内的授权卡片不会因为有了它就撤掉。** 通知可能被系统设置关掉、可能在
/// 专注助手里被折叠、也可能因为快捷方式没建成而根本发不出去 —— 授权是一条
/// 不能只剩一个入口的路。所以这层的每个实现都必须允许静默失败。
/// </summary>
public interface INativeNotifier
{
	/// <summary>这台机器上能不能真的弹出来。发不出去时为 false，调用方据此不必做别的处理。</summary>
	bool Available { get; }

	/// <summary>弹一条。失败不抛。</summary>
	void Show(ApprovalNotice notice);

	/// <summary>收掉某条。已经被用户点掉或系统清掉时是空操作。</summary>
	void Hide(string requestId);
}

/// <summary>不弹。非 Windows 平台与测试用它，省掉调用方到处判空。</summary>
public sealed class NullNativeNotifier : INativeNotifier
{
	public static readonly NullNativeNotifier Instance = new();

	public bool Available => false;

	public void Show(ApprovalNotice notice) { }

	public void Hide(string requestId) { }
}
