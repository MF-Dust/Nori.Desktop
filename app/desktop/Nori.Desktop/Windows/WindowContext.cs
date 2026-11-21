using Avalonia.Controls;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Windows;

/// <summary>原生窗口的桥接上下文。</summary>
internal sealed class WindowContext(Window owner, string label) : IBridgeSource
{
	public string Label { get; } = label;
	public Window? Self { get; } = owner;

	public void PostEvent(string name, object? payload) { }
}
