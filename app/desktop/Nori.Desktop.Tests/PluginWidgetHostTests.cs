using System.Reflection;
using Avalonia.Controls;
using Nori.Core.WebView;
using Nori.Desktop.Main;
using Nori.PluginRuntime;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private static PluginChatWidget TestWidget(string id = "io.nori.widget") =>
		new(id, "插件卡片", new Uri($"http://127.0.0.1:1234/secret/plugins/{id}/web/card.html"));

	[Theory]
	[InlineData("about:blank", true)]
	[InlineData("about:blank#changed", false)]
	[InlineData("https://example.com/", false)]
	[InlineData("file:///tmp/card.html", false)]
	[InlineData("http://127.0.0.1:1234/app/index.html", false)]
	[InlineData("data:text/html,hello", false)]
	public void 插件卡片顶层仅允许独立空白包装页(string url, bool allowed)
	{
		Assert.Equal(allowed, PluginWidgetHost.IsWrapperNavigation(new Uri(url)));
		Assert.False(PluginWidgetHost.IsWrapperNavigation(null));
	}

	[Fact]
	public Task 原生首页卡片默认折叠且快照刷新不重建入口() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new();
		PluginWidgetHost host = new(fixture._services);
		PluginChatWidget widget = TestWidget();
		host.ApplyWidgets([widget]);
		Expander card = Assert.Single(host.Children.OfType<Expander>());
		Assert.False(card.IsExpanded);
		Assert.Null(card.Content);
		host.ApplyWidgets([widget]);
		Assert.Same(card, Assert.Single(host.Children.OfType<Expander>()));
		host.ApplyWidgets([]);
		Assert.Empty(host.Children.OfType<Expander>());
		return Task.CompletedTask;
	});

	[Fact]
	public Task 安全模式不创建插件兼容页面或卡片() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PluginWidgetHost host = new(fixture._services);
		host.ApplyWidgets([TestWidget()]);
		Assert.Empty(host.Children.OfType<Expander>());
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("card")]
	[InlineData("hidden-card")]
	[InlineData("ancestor")]
	[InlineData("window")]
	[InlineData("remove")]
	[InlineData("detach")]
	public Task 折叠隐藏撤销或离开主页撤销卡片请求并移除内容(string closeKind) => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new();
		PluginWidgetHost host = new(fixture._services);
		StackPanel parent = new() {Children = {host}};
		Window window = new() {Content = parent};
		try
		{
			window.Show();
			host.ApplyWidgets([TestWidget()]);
			Expander card = Assert.Single(host.Children.OfType<Expander>());
			// 没有运行时的夹具走错误卡片，不创建真实平台 WebView。
			card.IsExpanded = true;
			Assert.NotNull(card.Content);
			CancellationTokenSource lifetime = new();
			CancellationToken token = lifetime.Token;
			using CancellationTokenRegistration registration = token.Register(() => throw new InvalidOperationException("插件取消回调失败"));
			WebViewScriptDispatcher scripts = new(_ => Task.FromResult<string?>(null));
			const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
			card.GetType().GetField("_lifetime", fields)!.SetValue(card, lifetime);
			card.GetType().GetField("_scripts", fields)!.SetValue(card, scripts);
			switch (closeKind)
			{
				case "card": card.IsExpanded = false; break;
				case "hidden-card": card.IsVisible = false; break;
				case "ancestor": parent.IsVisible = false; break;
				case "window": window.Hide(); break;
				case "remove": host.ApplyWidgets([]); break;
				case "detach": parent.Children.Remove(host); break;
			}
			Assert.True(token.IsCancellationRequested);
			Assert.True(scripts.IsClosed);
			Assert.Null(card.Content);
			Assert.Null(card.GetType().GetField("_lifetime", fields)!.GetValue(card));
			Assert.Null(card.GetType().GetField("_webView", fields)!.GetValue(card));
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 插件卡片加载失败保留其他卡片并允许重试() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new();
		PluginWidgetHost host = new(fixture._services);
		Window window = new() {Content = host};
		try
		{
			window.Show();
			host.ApplyWidgets([TestWidget(), TestWidget("io.nori.second")]);
			Expander[] cards = host.Children.OfType<Expander>().ToArray();
			cards[0].IsExpanded = true;
			StackPanel fallback = Assert.IsType<StackPanel>(cards[0].Content);
			Assert.Single(fallback.Children.OfType<Button>());
			Assert.False(cards[1].IsExpanded);
			Assert.Null(cards[1].Content);
			cards[0].IsExpanded = false;
			Assert.Null(cards[0].Content);
			cards[0].IsExpanded = true;
			Assert.Same(fallback, cards[0].Content);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});
}
