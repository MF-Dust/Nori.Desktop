using Avalonia.Controls;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Memory;

/// <summary>原生记忆窗口的独立可信来源；缓存可见性以供后台命令检查。</summary>
internal sealed class MemoryContext : WindowContext, INativeMemorySource
{
	public MemoryContext(Window owner) : base(owner, WindowLabels.Memory) { }
}
