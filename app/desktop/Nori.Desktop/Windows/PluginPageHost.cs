using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.PluginRuntime;

namespace Nori.Desktop.Windows;

/// <summary>窗口与设置共用的页面所有者；先同步卸下控件，再释放插件资源。</summary>
internal sealed class PluginPageHost : Decorator, IDisposable
{
	private IPluginPage? _page;
	private readonly Action<Exception> _report;

	internal PluginPageHost(IPluginPage page, Action<Exception> report)
	{
		Dispatcher.UIThread.VerifyAccess();
		_report = report;
		Control control = page.Control ?? throw new InvalidOperationException("插件页面未提供控件");
		if (control is TopLevel || control.Parent is not null || control.GetVisualParent() is not null)
			throw new InvalidOperationException("插件页面必须提供独占、未挂载且非窗口的控件");
		Child = control;
		_page = page;
	}

	public void Dispose()
	{
		Dispatcher.UIThread.VerifyAccess();
		Child = null;
		IPluginPage? page = _page;
		_page = null;
		try { page?.Dispose(); }
		catch (Exception exception) { _report(exception); }
	}
}
