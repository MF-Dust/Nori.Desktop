using Avalonia.Controls;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接命令的来源窗口边界
///
/// 抽象 BridgeCommands 需要的窗口能力 (标签、可见性、原生 Window、事件/结果回推),
/// 真实实现是 NoriWindow, 测试可提供替身.
/// </summary>
public interface IBridgeSource
{
	/// <summary>窗口标签</summary>
	string Label { get; }

	/// <summary>窗口是否可见</summary>
	bool IsVisible { get; }

	/// <summary>底层 Avalonia 窗口 (剪贴板/存储选择器等 TopLevel 能力); 测试替身可为 null</summary>
	Window? Self { get; }

	/// <summary>向该窗口的页面推送一个事件</summary>
	void PostEvent(string name, object? payload);

	/// <summary>回复一次 invoke 调用</summary>
	void PostResult(long id, object? value, string? error);
}

/// <summary>
/// 原生设置窗口的可信桥接上下文标记。
///
/// 该接口不对 WebView 暴露，且不会伪装成 main 窗口；只有 SettingsService
/// 能创建实现，BridgeCommands 也只在显式允许的设置命令中接受它。
/// </summary>
internal interface INativeSettingsSource : IBridgeSource
{
}

/// <summary>原生记忆窗口的可信来源；服务及宿主入口共同限制固定的记忆命令白名单。</summary>
internal interface INativeMemorySource : IBridgeSource
{
}

/// <summary>原生模型窗口的可信来源；服务及宿主入口共同限制固定的模型命令白名单。</summary>
internal interface INativeModelSource : IBridgeSource
{
}
