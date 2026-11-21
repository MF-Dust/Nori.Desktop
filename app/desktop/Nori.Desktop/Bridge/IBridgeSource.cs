using Avalonia.Controls;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接命令的来源窗口上下文。
/// 真实实现是 WindowContext / NativeChatContext，测试可提供替身。
/// </summary>
public interface IBridgeSource
{
	/// <summary>窗口标签</summary>
	string Label { get; }

	/// <summary>底层 Avalonia 窗口 (剪贴板/存储选择器等 TopLevel 能力); 测试替身可为 null</summary>
	Window? Self { get; }

	/// <summary>来源生命周期取消令牌；窗口销毁时取消关联会话。</summary>
	CancellationToken LifetimeToken => CancellationToken.None;

	/// <summary>向该窗口推送事件</summary>
	void PostEvent(string name, object? payload);
}
