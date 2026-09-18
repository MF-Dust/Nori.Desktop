using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginWidgetBridgeTests
{
	private const string PluginId = "io.nori.widget";

	private static string Request(PluginWidgetBridge bridge, string fields = "") =>
		$$"""{"transportToken":"{{bridge.TransportToken}}","source":"nori-plugin-widget","requestId":7,"actionId":"refresh"{{fields}}} """;

	[Fact]
	public async Task 卡片身份来自宿主且结果保留公开协议()
	{
		string? invokedPlugin = null;
		string? invokedAction = null;
		JsonNode? invokedArgs = null;
		PluginWidgetBridge bridge = new(PluginId, (plugin, action, args, _) =>
		{
			invokedPlugin = plugin;
			invokedAction = action;
			invokedArgs = args;
			return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
		});
		string? reply = await bridge.InvokeAsync(Request(bridge, ",\"args\":{\"value\":3}"), CancellationToken.None);
		Assert.Equal(PluginId, invokedPlugin);
		Assert.Equal("refresh", invokedAction);
		Assert.Equal(3, invokedArgs!["value"]!.GetValue<int>());
		using JsonDocument result = JsonDocument.Parse(Assert.IsType<string>(reply));
		Assert.Equal("nori-plugin-widget-host", result.RootElement.GetProperty("source").GetString());
		Assert.Equal(7, result.RootElement.GetProperty("requestId").GetInt64());
		Assert.True(result.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
		Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("error").ValueKind);
		Assert.DoesNotContain(bridge.TransportToken, reply);
	}

	[Theory]
	[InlineData(",\"pluginId\":\"io.nori.other\"")]
	[InlineData(",\"pluginId\":null")]
	[InlineData(",\"cmd\":\"settings_update_ai\"")]
	[InlineData(",\"kind\":\"invoke\"")]
	[InlineData(",\"command\":\"settings_update_ai\"")]
	[InlineData(",\"event\":\"audio:play\"")]
	[InlineData(",\"args\":[]")]
	[InlineData(",\"args\":\"bad\"")]
	[InlineData(",\"actionId\":\" \"")]
	public async Task 越权身份主桥接信封和错误参数不会调用插件(string fields)
	{
		int calls = 0;
		PluginWidgetBridge bridge = new(PluginId, (_, _, _, _) => { calls++; return Task.FromResult<JsonNode?>(null); });
		string? reply = await bridge.InvokeAsync(Request(bridge, fields), CancellationToken.None);
		Assert.Equal(0, calls);
		using JsonDocument result = JsonDocument.Parse(Assert.IsType<string>(reply));
		Assert.Equal("插件动作不可用或执行失败", result.RootElement.GetProperty("error").GetString());
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("not-json")]
	[InlineData("{\"source\":\"nori-plugin-widget\",\"requestId\":7,\"actionId\":\"refresh\"}")]
	public async Task 缺少包装页凭据的直接原生调用被丢弃(string raw)
	{
		int calls = 0;
		PluginWidgetBridge bridge = new(PluginId, (_, _, _, _) => { calls++; return Task.FromResult<JsonNode?>(null); });
		Assert.Null(await bridge.InvokeAsync(raw, CancellationToken.None));
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task 旧卡片的凭据不能用于重新展开后的通道()
	{
		int calls = 0;
		Task<JsonNode?> Invoke(string _, string __, JsonNode? ___, CancellationToken ____) { calls++; return Task.FromResult<JsonNode?>(null); }
		PluginWidgetBridge oldBridge = new(PluginId, Invoke);
		PluginWidgetBridge newBridge = new(PluginId, Invoke);
		Assert.NotEqual(oldBridge.TransportToken, newBridge.TransportToken);
		Assert.Null(await newBridge.InvokeAsync(Request(oldBridge), CancellationToken.None));
		Assert.Equal(0, calls);
	}

	[Theory]
	[InlineData("9007199254740992")]
	[InlineData("-1")]
	[InlineData("1.5")]
	[InlineData("\"7\"")]
	public async Task 非安全整数请求编号被丢弃(string id)
	{
		PluginWidgetBridge bridge = new(PluginId, (_, _, _, _) => throw new InvalidOperationException("不应调用"));
		Assert.Null(await bridge.InvokeAsync(Request(bridge).Replace("\"requestId\":7", $"\"requestId\":{id}", StringComparison.Ordinal), CancellationToken.None));
	}

	[Fact]
	public async Task 超大请求被丢弃且插件异常不泄漏详情()
	{
		PluginWidgetBridge bridge = new(PluginId, (_, _, _, _) => throw new InvalidOperationException("secret-key /private/path"));
		Assert.Null(await bridge.InvokeAsync(new string(' ', 65_537), CancellationToken.None));
		string? reply = await bridge.InvokeAsync(Request(bridge), CancellationToken.None);
		Assert.DoesNotContain("secret-key", reply);
		Assert.DoesNotContain("/private/path", reply);
		Assert.Contains("error", reply);
	}

	[Fact]
	public async Task 页面取消会阻断动作并丢弃不合作插件的迟到结果()
	{
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<JsonNode?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PluginWidgetBridge bridge = new(PluginId, (_, _, _, _) => { entered.SetResult(); return release.Task; });
		using CancellationTokenSource lifetime = new();
		Task<string?> pending = bridge.InvokeAsync(Request(bridge), lifetime.Token);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		lifetime.Cancel();
		release.SetResult(new JsonObject { ["stale"] = true });
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.InvokeAsync(Request(bridge), lifetime.Token));
	}

	[Theory]
	[InlineData("https://example.com/plugins/io.nori.widget/web/card.html")]
	[InlineData("http://127.0.0.1:1234/secret/plugins/io.nori.other/web/card.html")]
	[InlineData("http://127.0.0.1:1234/plugins/io.nori.widget/web/card.html?next=other")]
	[InlineData("file:///plugins/io.nori.widget/web/card.html")]
	public void 卡片拒绝外网文件和跨插件入口(string entry)
	{
		Assert.Throws<InvalidOperationException>(() => PluginWidgetBridge.CreateDocument(new PluginChatWidget(PluginId, "卡片", new Uri(entry)), "token"));
	}

	[Fact]
	public void 卡片拒绝回环地址中的用户信息()
	{
		// 本地资源服务使用 HTTP；此处仅构造恶意 URI 验证拒绝，不发送网络请求。
		UriBuilder entry = new("http://127.0.0.1:1234/secret/plugins/io.nori.widget/web/card.html")
		{
			UserName = "user",
		};
		Assert.NotEmpty(entry.Uri.UserInfo);
		Assert.Throws<InvalidOperationException>(() => PluginWidgetBridge.CreateDocument(
			new PluginChatWidget(PluginId, "卡片", entry.Uri), "token"));
	}

	[Fact]
	public void 包装页隔离来源且标题不会注入标记()
	{
		PluginChatWidget widget = new(PluginId, "\"><script>alert(1)</script>", new Uri($"http://127.0.0.1:1234/secret/plugins/{PluginId}/web/card.html"));
		string html = PluginWidgetBridge.CreateDocument(widget, "token");
		Assert.Contains("sandbox=\"allow-scripts\"", html);
		Assert.DoesNotContain("allow-same-origin", html);
		Assert.Contains("event.source !== frame.contentWindow", html);
		Assert.Contains("event.origin !== \"null\"", html);
		Assert.Contains("&lt;script&gt;", html);
		Assert.DoesNotContain("<script>alert(1)</script>", html);
		Assert.DoesNotContain("window.__nori.", html);
	}
}
