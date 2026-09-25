using Avalonia.Controls;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Models;

/// <summary>原生模型窗口的独立可信来源；缓存可见性以供后台命令检查。</summary>
internal sealed class ModelContext : WindowContext, INativeModelSource
{
	public ModelContext(Window owner) : base(owner, WindowLabels.Models) { }
}
