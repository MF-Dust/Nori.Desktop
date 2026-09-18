using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.PluginRuntime;

/// <summary>
/// 首页卡片的独立动作通道。身份由宿主绑定，不转发主桥接命令，也不采信页面的 pluginId。
/// </summary>
internal sealed class PluginWidgetBridge
{
	private readonly string _pluginId;
	private readonly Func<string, string, JsonNode?, CancellationToken, Task<JsonNode?>> _invoke;
	public string TransportToken { get; } = Guid.NewGuid().ToString("N");

	public PluginWidgetBridge(string pluginId, Func<string, string, JsonNode?, CancellationToken, Task<JsonNode?>> invoke)
	{
		PluginWindowHost.ValidatePluginId(pluginId, nameof(pluginId));
		_pluginId = pluginId;
		_invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
	}

	/// <summary>仅处理卡片动作协议；非法信封不进入插件，更不会进入 NoriBridge。</summary>
	public async Task<string?> InvokeAsync(string raw, CancellationToken cancellationToken)
	{
		if (raw.Length > 65_536) return null;
		JsonDocument document;
		try { document = JsonDocument.Parse(raw, new JsonDocumentOptions {MaxDepth = 32}); }
		catch (JsonException) { return null; }
		using (document)
		{
			JsonElement request = document.RootElement;
			if (request.ValueKind != JsonValueKind.Object
				|| !request.TryGetProperty("transportToken", out JsonElement transport)
				|| transport.ValueKind != JsonValueKind.String || transport.GetString() != TransportToken
				|| !request.TryGetProperty("source", out JsonElement source)
				|| source.ValueKind != JsonValueKind.String || source.GetString() != "nori-plugin-widget"
				|| !request.TryGetProperty("requestId", out JsonElement id)
				|| id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long requestId)
				|| requestId is < 0 or > 9_007_199_254_740_991)
				return null;

			JsonNode? result = null;
			string? error = null;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (request.TryGetProperty("cmd", out _) || request.TryGetProperty("kind", out _)
					|| request.TryGetProperty("command", out _) || request.TryGetProperty("event", out _)
					|| (request.TryGetProperty("pluginId", out JsonElement identity)
						&& (identity.ValueKind != JsonValueKind.String || identity.GetString() != _pluginId))
					|| !request.TryGetProperty("actionId", out JsonElement action)
					|| action.ValueKind != JsonValueKind.String
					|| string.IsNullOrWhiteSpace(action.GetString()) || action.GetString()!.Length > 256)
					throw new InvalidOperationException("卡片动作参数无效");
				JsonNode? args = null;
				if (request.TryGetProperty("args", out JsonElement arguments) && arguments.ValueKind != JsonValueKind.Null)
				{
					if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("卡片动作参数无效");
					args = JsonNode.Parse(arguments.GetRawText());
				}
				result = await _invoke(_pluginId, action.GetString()!, args, cancellationToken).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception)
			{
				// 不把插件异常中的本地路径、参数或凭据发回页面。
				error = "插件动作不可用或执行失败";
			}
			return JsonSerializer.Serialize(new {source = "nori-plugin-widget-host", requestId, result, error});
		}
	}

	/// <summary>
	/// 在无主桥接的 about:blank 文档中承载卡片。卡片运行于 opaque origin，
	/// 不允许存储、弹窗、下载或顶层导航；消息核验来源窗口，传输凭据只留在包装页闭包中。
	/// </summary>
	public static string CreateDocument(PluginChatWidget widget, string transportToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(transportToken);
		PluginWindowHost.ValidatePluginId(widget.PluginId, nameof(widget.PluginId));
		Uri entry = widget.EntryUrl;
		if (!entry.IsAbsoluteUri || entry.Scheme != "http" || !entry.IsLoopback
			|| !string.IsNullOrEmpty(entry.UserInfo) || !string.IsNullOrEmpty(entry.Query) || !string.IsNullOrEmpty(entry.Fragment)
			|| !entry.AbsolutePath.EndsWith($"/plugins/{widget.PluginId}/web/card.html", StringComparison.Ordinal))
			throw new InvalidOperationException("插件卡片必须来自宿主回环资源服务");
		string nonce = Guid.NewGuid().ToString("N");
		return $$"""
			<!doctype html><html><head><meta charset="utf-8">
			<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-{{nonce}}'; style-src 'unsafe-inline'; frame-src {{WebUtility.HtmlEncode(entry.AbsoluteUri)}}; base-uri 'none'; form-action 'none'">
			<style>html,body,iframe{margin:0;border:0;width:100%;height:100%;overflow:hidden}iframe{display:block}</style>
			</head><body>
			<iframe title="{{WebUtility.HtmlEncode(widget.Title)}}" sandbox="allow-scripts" referrerpolicy="no-referrer"></iframe>
			<script nonce="{{nonce}}">
			(() => {
			const frame = document.querySelector("iframe");
			const pluginId = {{JsonSerializer.Serialize(widget.PluginId)}};
			const transportToken = {{JsonSerializer.Serialize(transportToken)}};
			window.addEventListener("message", event => {
				if (event.source !== frame.contentWindow || event.origin !== "null") return;
				const data = event.data;
				if (!data || data.source !== "nori-plugin-widget" || !Number.isSafeInteger(data.requestId) || data.requestId < 0) return;
				if ("cmd" in data || "kind" in data || "command" in data || "event" in data || ("pluginId" in data && data.pluginId !== pluginId)) return;
				if (typeof window.invokeCSharpAction !== "function") return;
				window.invokeCSharpAction(JSON.stringify({transportToken, source: data.source, requestId: data.requestId, actionId: data.actionId, args: data.args}));
			});
			window.__noriWidgetReply = json => frame.contentWindow.postMessage(JSON.parse(json), "*");
			frame.addEventListener("load", () => {
				if (typeof window.invokeCSharpAction === "function") window.invokeCSharpAction("widget-loaded:" + transportToken);
			});
			frame.src = {{JsonSerializer.Serialize(entry.AbsoluteUri)}};
			})();
			</script></body></html>
			""";
	}
}
