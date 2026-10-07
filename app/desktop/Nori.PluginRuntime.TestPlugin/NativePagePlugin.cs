using Avalonia.Controls;
using Avalonia.Threading;
using System.Text.Json.Nodes;

namespace Nori.PluginRuntime.TestPlugin;

/// <summary>真实 ALC 页面测试插件；只使用框架控件，避免插件自定义控件的全局类型缓存。</summary>
public sealed class NativePagePlugin : INoriPlugin
{
	private int _created;
	private int _disposed;
	private readonly List<IPluginRegistration> _pages = [];

	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_pages.Add(context.Contributions.Register(new PageContribution(this, "home", "原生页面 · Native page")));
		_pages.Add(context.Contributions.Register(new PageContribution(this, "broken", "工厂错误")));
		_pages.Add(context.Contributions.Register(new PageContribution(this, "dispose-error", "释放错误")));
		context.Contributions.Register(new StateContribution(this));
		return ValueTask.CompletedTask;
	}

	public ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		if (_created != _disposed) throw new InvalidOperationException("停用前页面尚未释放");
		return ValueTask.CompletedTask;
	}

	private sealed class PageContribution(NativePagePlugin plugin, string id, string title) : IPluginPageContribution
	{
		public string Id => id;
		public string Title => title;
		public IPluginPage CreatePage()
		{
			Dispatcher.UIThread.VerifyAccess();
			if (id == "broken") throw new InvalidOperationException("测试工厂异常");
			plugin._created++;
			return new Page(plugin, id == "dispose-error");
		}
	}

	private sealed class Page(NativePagePlugin plugin, bool throwOnDispose) : IPluginPage
	{
		public Control Control { get; } = new StackPanel
		{
			Margin = new Avalonia.Thickness(24), Spacing = 16,
			Children = { new TextBlock { Text = "插件原生页面 · Native plugin page", FontSize = 24 }, new TextBlock { Text = "独立窗口 / Native Avalonia window" } },
		};
		public void Dispose()
		{
			Dispatcher.UIThread.VerifyAccess();
			((StackPanel)Control).Children.Clear();
			plugin._disposed++;
			if (throwOnDispose) throw new InvalidOperationException("测试释放异常");
		}
	}

	private sealed class StateContribution(NativePagePlugin plugin) : IPluginActionContribution
	{
		public string Id => "state";
		public string Description => "测试页面状态";
		public JsonNode? ParametersSchema => null;
		public Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken)
		{
			if (arguments?["revoke"]?.GetValue<bool>() == true)
			{
				foreach (IPluginRegistration registration in plugin._pages) registration.Dispose();
				plugin._pages.Clear();
			}
			return Task.FromResult<JsonObject?>(new JsonObject { ["created"] = plugin._created, ["disposed"] = plugin._disposed });
		}
	}
}
