using System.Text.Json.Nodes;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	// ===================================================================
	// 事件出口
	// ===================================================================

	/// <summary>原生会话只回推最初的可信对象，旧会话不会流入同标签的新窗口。</summary>
	private void PostAgentEvent(IBridgeSource source, object payload)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		if (source is not INativeChatSource native) return;
		if (!Nori.Desktop.Chat.NativeChatService.IsTrustedSource(native) || native.LifetimeToken.IsCancellationRequested) return;
		try { source.PostEvent(AgentEventName, payload); }
		catch { /* 窗口退出不影响会话收尾。 */ }
	}

	/// <summary>自动化状态变化时刷新脱敏快照。</summary>
	private void OnAutomationChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		InvalidateSnapshot();
	}

	private void OnUpdateStatusChanged()
	{
		if (Volatile.Read(ref _disposed) == 0) InvalidateSnapshot();
	}

	private static JsonNode? ToJsonNode(object? value)
	{
		if (value is null) return null;
		try
		{
			return JsonSerializerNode(value);
		}
		catch
		{
			return System.Text.Json.Nodes.JsonValue.Create(value.ToString());
		}
	}

	private static System.Text.Json.Nodes.JsonNode? JsonSerializerNode(object value)
	{
		string json = System.Text.Json.JsonSerializer.Serialize(value, BridgeJson.Options);
		return System.Text.Json.Nodes.JsonNode.Parse(json);
	}
}
