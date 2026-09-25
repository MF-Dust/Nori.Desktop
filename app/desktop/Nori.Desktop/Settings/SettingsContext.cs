using Avalonia.Controls;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Settings;

/// <summary>
/// 原生设置窗口的桥接上下文。
///
/// 该上下文以 settings 标签标识自身，不冒充 main WebView；可见性通过窗口属性
/// 缓存，避免后台命令直接读取 Avalonia 对象。
/// </summary>
internal sealed class SettingsContext : WindowContext, INativeSettingsSource
{
	public SettingsContext(Window owner) : base(owner, WindowLabels.Settings) { }
}
