using Avalonia.Controls;
using Avalonia.Threading;
using Nori.PluginRuntime;

namespace Nori.Desktop.Windows;

/// <summary>纯元数据，不持有插件工厂或控件。</summary>
public sealed record PluginPageInfo(string PluginId, string Id, string Title);

/// <summary>原生页面所有者；撤销通知返回前完成窗口与内嵌页面的 UI 清理。</summary>
internal sealed class PluginPageController : IDisposable
{
	private sealed record Registration(IPluginPageContribution Contribution, PluginPageInfo Info, PluginPagePlacement Placement)
	{
		internal bool Matches(Registration other) => ReferenceEquals(Contribution, other.Contribution) && Info == other.Info && Placement == other.Placement;
	}

	private sealed class SettingsEntry(Registration registration)
	{
		internal Registration Registration { get; } = registration;
		internal PluginPageHost? Host { get; set; }
	}

	private readonly PluginRuntimeHost _runtime;
	private readonly Action<Exception> _report;
	private readonly Dictionary<(string Plugin, string Page), (Registration Registration, PluginPageWindow Window)> _windows = [];
	private readonly Dictionary<(string Plugin, string Page), SettingsEntry> _settings = [];
	private bool _disposed;

	internal PluginPageController(PluginRuntimeHost runtime, Action<Exception> report)
	{
		_runtime = runtime;
		_report = report;
		_runtime.ContributionsChanged += OnContributionsChanged;
		Reconcile();
	}

	internal event Action? SettingsPagesChanged;

	internal IReadOnlyList<PluginPageInfo> Pages => _disposed ? [] : GetRegistrations()
		.Where(item => item.Placement == PluginPagePlacement.Window).Select(item => item.Info).ToArray();

	internal IReadOnlyList<PluginPageInfo> SettingsPages => _settings.Values.Select(item => item.Registration.Info).ToArray();

	private Registration[] GetRegistrations() => _runtime.GetContributionsWithSource<IPluginPageContribution>()
		.Select(item => new Registration(item.Contribution, new PluginPageInfo(item.Plugin.Id, item.Contribution.Id, item.Contribution.Title), item.Contribution.Placement)).ToArray();

	internal PluginPageWindow Open(string pluginId, string pageId)
	{
		Dispatcher.UIThread.VerifyAccess();
		ObjectDisposedException.ThrowIf(_disposed, this);
		Registration registration = GetRegistrations().FirstOrDefault(item => item.Info.PluginId == pluginId && item.Info.Id == pageId && item.Placement == PluginPagePlacement.Window)
			?? throw new InvalidOperationException("插件页面不存在或插件已停用");
		var key = (pluginId, pageId);
		if (_windows.TryGetValue(key, out var existing))
		{
			if (existing.Registration.Matches(registration))
			{
				if (existing.Window.WindowState == WindowState.Minimized) existing.Window.WindowState = WindowState.Normal;
				existing.Window.Show();
				existing.Window.Activate();
				return existing.Window;
			}
			existing.Window.Close();
		}
		PluginPageHost host = CreateHost(registration);
		PluginPageWindow? window = null;
		try
		{
			window = new PluginPageWindow(registration.Info.Title, host);
			VerifyRegistered(registration);
			_windows[key] = (registration, window);
			window.Closed += (_, _) =>
			{
				if (_windows.TryGetValue(key, out var current) && ReferenceEquals(current.Window, window)) _windows.Remove(key);
			};
			window.Show();
			window.Activate();
			return window;
		}
		catch (Exception exception)
		{
			if (window is not null) window.Close();
			host.Dispose();
			throw new InvalidOperationException("插件页面打开失败", exception);
		}
	}

	internal PluginPageHost OpenSettingsPage(PluginPageInfo info)
	{
		Dispatcher.UIThread.VerifyAccess();
		ObjectDisposedException.ThrowIf(_disposed, this);
		var key = (info.PluginId, info.Id);
		if (!_settings.TryGetValue(key, out SettingsEntry? entry) || !ReferenceEquals(info, entry.Registration.Info))
			throw new InvalidOperationException("插件页面不存在或插件已停用");
		if (entry.Host is not null) return entry.Host;
		try
		{
			PluginPageHost host = CreateHost(entry.Registration);
			// 工厂或挂载回调可能同步撤销自己的注册，不能留下刚创建的孤儿页面。
			if (!_settings.TryGetValue(key, out SettingsEntry? current) || !ReferenceEquals(entry, current))
			{
				host.Dispose();
				throw new InvalidOperationException("插件页面已撤销");
			}
			entry.Host = host;
			return host;
		}
		catch (Exception exception)
		{
			_report(exception);
			throw;
		}
	}

	private PluginPageHost CreateHost(Registration registration)
	{
		IPluginPage? page = null;
		PluginPageHost? host = null;
		try
		{
			VerifyRegistered(registration);
			page = registration.Contribution.CreatePage() ?? throw new InvalidOperationException("插件页面工厂返回了空页面");
			VerifyRegistered(registration);
			host = new PluginPageHost(page, _report);
			VerifyRegistered(registration);
			return host;
		}
		catch (Exception exception)
		{
			if (host is not null) host.Dispose();
			else
			{
				try { page?.Dispose(); }
				catch (Exception disposeError) { _report(disposeError); }
			}
			throw new InvalidOperationException("插件页面打开失败", exception);
		}
	}

	private void VerifyRegistered(Registration registration)
	{
		if (_disposed || !GetRegistrations().Any(registration.Matches)) throw new InvalidOperationException("插件页面已撤销");
	}

	private void OnContributionsChanged()
	{
		if (Dispatcher.UIThread.CheckAccess()) Reconcile();
		else Dispatcher.UIThread.InvokeAsync(Reconcile).GetAwaiter().GetResult();
	}

	private void Reconcile()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_disposed) return;
		Registration[] active = GetRegistrations();
		List<PluginPageWindow> windows = [];
		List<PluginPageHost> hosts = [];
		bool changed = false;
		foreach (var key in _windows.Keys.ToArray())
		{
			if (active.Any(_windows[key].Registration.Matches)) continue;
			windows.Add(_windows[key].Window);
			_windows.Remove(key);
		}
		foreach (var key in _settings.Keys.ToArray())
		{
			if (active.Any(_settings[key].Registration.Matches)) continue;
			if (_settings[key].Host is { } host) hosts.Add(host);
			_settings.Remove(key);
			changed = true;
		}
		foreach (Registration registration in active.Where(item => item.Placement == PluginPagePlacement.Settings))
		{
			if (_settings.TryAdd((registration.Info.PluginId, registration.Info.Id), new SettingsEntry(registration))) changed = true;
		}
		// 先更新注册快照，再调用插件释放代码，允许释放期间再次撤销其他贡献。
		foreach (PluginPageWindow window in windows) window.Close();
		foreach (PluginPageHost host in hosts) host.Dispose();
		if (changed) SettingsPagesChanged?.Invoke();
	}

	public void Dispose()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_disposed) return;
		_disposed = true;
		_runtime.ContributionsChanged -= OnContributionsChanged;
		PluginPageWindow[] windows = _windows.Values.Select(item => item.Window).ToArray();
		PluginPageHost[] hosts = _settings.Values.Select(item => item.Host).OfType<PluginPageHost>().ToArray();
		_windows.Clear();
		_settings.Clear();
		foreach (PluginPageWindow window in windows) window.Close();
		foreach (PluginPageHost host in hosts) host.Dispose();
		SettingsPagesChanged?.Invoke();
	}
}
