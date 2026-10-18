using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nori.PluginRuntime;

namespace Nori.PluginRuntime.TestPlugin;

/// <summary>真实 ALC 聊天提供商插件：验证宿主注册、类型身份共享、模型清洗与撤销取消。</summary>
public sealed class ChatProviderPlugin : INoriPlugin
{
	private IPluginRegistration? _registration;

	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_registration = context.Contributions.Register(new Provider(context.StoppingToken));
		return ValueTask.CompletedTask;
	}

	public ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		_registration?.Dispose();
		_registration = null;
		return ValueTask.CompletedTask;
	}

	private sealed class Provider(CancellationToken stoppingToken) : IPluginChatProviderContribution
	{
		public string Id => "local";
		public string Name => "测试本地提供商";
		public string DefaultBaseUrl => "";
		public bool RequiresApiKey => false;

		public IChatClient CreateClient(PluginChatClientOptions options) => new Client();

		async ValueTask<IReadOnlyList<string>> IPluginChatProviderContribution.GetModelsAsync(PluginChatClientOptions options, CancellationToken cancellationToken)
		{
			// apiKey=fast 由测试直接取模型列表；其余请求保持等待，用于验证撤销会取消在途查询。
			if (options.ApiKey == "fast") return ["model-1", "model-1", " ", "bad\u0001id", "model-2"];
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationToken);
			await Task.Delay(Timeout.Infinite, linked.Token);
			return [];
		}

		private sealed class Client : IChatClient
		{
			public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
				Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "插件回复")));

			public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
				IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
				ChatOptions? options = null,
				[EnumeratorCancellation] CancellationToken cancellationToken = default)
			{
				await Task.Yield();
				yield return new ChatResponseUpdate(ChatRole.Assistant, "插件流式回复");
			}

			public object? GetService(Type serviceType, object? serviceKey = null) => null;

			public void Dispose()
			{
			}
		}
	}
}

/// <summary>真实 ALC 应用互操作插件；验证能力注册、查询与撤销。</summary>
public sealed class ApplicationPlugin : INoriPlugin
{
	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		IPluginApplicationCapability capability = context.Capabilities.GetRequired<IPluginApplicationCapability>();
		context.Contributions.Register(new ApplicationAction(capability));
		return ValueTask.CompletedTask;
	}

	public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

	private sealed class ApplicationAction(IPluginApplicationCapability capability) : IPluginActionContribution
	{
		public string Id => "applications";
		public string Description => "测试应用互操作";
		public JsonNode? ParametersSchema => null;

		public async Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken)
		{
			IReadOnlyList<PluginApplicationInfo> running = await capability.GetRunningAsync(cancellationToken);
			return new JsonObject { ["count"] = running.Count, ["name"] = running.Count > 0 ? running[0].Name : "" };
		}
	}
}
