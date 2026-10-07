using Avalonia.Controls;
using Avalonia.Threading;
using Nori.PluginRuntime;

namespace Nori.Desktop.Windows;

/// <summary>纯元数据，不持有插件工厂或控件。</summary>
public sealed record PluginPageInfo(string PluginId, string Id, string Title);

/// <summary>原生页面窗口所有者；撤销通知返回前完成 UI 清理。</summary>
internal sealed class PluginPageController : IDisposable
{
	private readonly PluginRuntimeHost _runtime;
	private readonly Action<Exception> _report;
	private readonly Dictionary<(string Plugin, string Page), PluginPageWindow> _windows = [];
	private bool _disposed;

	internal PluginPageController(PluginRuntimeHost runtime, Action<Exception> report)
	{
		_runtime = runtime;
		_report = report;
		_runtime.ContributionsChanged += OnContributionsChanged;
	}

	internal IReadOnlyList<PluginPageInfo> Pages => _runtime.GetContributionsWithSource<IPluginPageContribution>()
		.Select(item => new PluginPageInfo(item.Plugin.Id, item.Contribution.Id, item.Contribution.Title)).ToArray();

	internal PluginPageWindow Open(string pluginId, string pageId)
	{
		Dispatcher.UIThread.VerifyAccess();
		ObjectDisposedException.ThrowIf(_disposed, this);
		var key = (pluginId, pageId);
		if (_windows.TryGetValue(key, out PluginPageWindow? existing))
		{
			if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
			existing.Show();
			existing.Activate();
			return existing;
		}
		IPluginPageContribution? contribution = _runtime.GetContributionsWithSource<IPluginPageContribution>()
			.Where(item => item.Plugin.Id == pluginId && item.Contribution.Id == pageId).Select(item => item.Contribution).FirstOrDefault();
		if (contribution is null) throw new InvalidOperationException("插件页面不存在或插件已停用");
		IPluginPage? page = null;
		PluginPageWindow? window = null;
		try
		{
			page = contribution.CreatePage() ?? throw new InvalidOperationException("插件页面工厂返回了空页面");
			if (!_runtime.GetContributionsWithSource<IPluginPageContribution>().Any(item => item.Plugin.Id == pluginId && ReferenceEquals(item.Contribution, contribution)))
				throw new InvalidOperationException("插件页面已撤销");
			window = new PluginPageWindow(contribution.Title, page, _report);
			_windows.Add(key, window);
			window.Closed += (_, _) => _windows.Remove(key);
			window.Show();
			window.Activate();
			return window;
		}
		catch (Exception exception)
		{
			if (window is not null) window.Close();
			else
			{
				try { page?.Dispose(); }
				catch (Exception disposeError) { _report(disposeError); }
			}
			throw new InvalidOperationException("插件页面打开失败", exception);
		}
	}

	private void OnContributionsChanged()
	{
		if (Dispatcher.UIThread.CheckAccess()) Reconcile();
		else Dispatcher.UIThread.InvokeAsync(Reconcile).GetAwaiter().GetResult();
	}

	private void Reconcile()
	{
		if (_disposed) return;
		HashSet<(string, string)> active = Pages.Select(page => (page.PluginId, page.Id)).ToHashSet();
		foreach (var key in _windows.Keys.Where(key => !active.Contains(key)).ToArray()) _windows[key].Close();
	}

	public void Dispose()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_disposed) return;
		_disposed = true;
		_runtime.ContributionsChanged -= OnContributionsChanged;
		foreach (PluginPageWindow window in _windows.Values.ToArray()) window.Close();
		_windows.Clear();
	}
}
