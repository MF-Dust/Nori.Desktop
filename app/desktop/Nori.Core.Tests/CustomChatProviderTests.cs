using Microsoft.Extensions.AI;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class CustomChatProviderTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-custom-provider");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public CustomChatProviderTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
	}

	private static CustomChatProviderDescriptor Descriptor(string id = "plugin:local.plugin:mock", bool requiresApiKey = true) =>
		new(id, "本地测试提供商", "", requiresApiKey);

	private static IChatClient Client(Func<string> reply) => new ReplyClient(reply);

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}

	[Fact]
	public void 注册保留原始ID并拒绝重复与非法前缀()
	{
		CustomChatProviderRegistry registry = new();
		int created = 0;
		registry.Register(Descriptor(), (_, _, _) => { created++; return Client(() => "ok"); }, (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["m1"]));

		Assert.True(registry.TryGet("plugin:local.plugin:mock", out CustomChatProviderDescriptor? descriptor));
		Assert.Equal("本地测试提供商", descriptor!.Name);
		Assert.False(descriptor.RequiresApiKey && false);
		Assert.Throws<InvalidOperationException>(() =>
			registry.Register(Descriptor(), (_, _, _) => Client(() => "other"), (_, _, _) => Task.FromResult<IReadOnlyList<string>>([])));
		Assert.Throws<ArgumentException>(() =>
			registry.Register(new CustomChatProviderDescriptor("openai", "伪装", "", false),
				(_, _, _) => Client(() => "x"), (_, _, _) => Task.FromResult<IReadOnlyList<string>>([])));
		Assert.Throws<ArgumentException>(() =>
			registry.Register(new CustomChatProviderDescriptor("plugin:a:b:c", "层级错误", "", false),
				(_, _, _) => Client(() => "x"), (_, _, _) => Task.FromResult<IReadOnlyList<string>>([])));
		Assert.Equal(0, created);
	}

	[Fact]
	public async Task 撤销后立即拒绝新建客户端且不残留旧注册()
	{
		CustomChatProviderRegistry registry = new();
		registry.Register(Descriptor(), (_, _, _) => Client(() => "ok"), (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));
		ILlmAdapter adapter = registry.CreateAdapter("plugin:local.plugin:mock");

		Assert.True(registry.Unregister("plugin:local.plugin:mock"));
		ChatException error = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.CompleteAsync("", "", "model", "system", [new ChatMessageInput("user", "hi")]));
		Assert.Contains("plugin:local.plugin:mock", error.Message, StringComparison.Ordinal);
		Assert.False(registry.TryGet("plugin:local.plugin:mock", out _));
		Assert.Empty(registry.GetProviders());
	}

	[Fact]
	public async Task 空地址允许本地客户端且无需密钥由宿主配置决定()
	{
		CustomChatProviderRegistry registry = new();
		string? factoryUrl = null;
		string? factoryKey = null;
		registry.Register(Descriptor(requiresApiKey: false), (baseUrl, apiKey, _) =>
		{
			factoryUrl = baseUrl;
			factoryKey = apiKey;
			return Client(() => "pong");
		}, (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["local-model"]));

		string reply = await registry.CreateAdapter("plugin:local.plugin:mock")
			.CompleteAsync("", "", "local-model", "system", [new ChatMessageInput("user", "ping")]);

		Assert.Equal("pong", reply);
		Assert.Equal("", factoryUrl);
		Assert.Equal("", factoryKey);
		Assert.Equal(["local-model"], await registry.FetchModelsAsync("plugin:local.plugin:mock", "", ""));
	}

	[Fact]
	public async Task 需要密钥但配置为空时明确失败而不是回退内置服务()
	{
		CustomChatProviderRegistry registry = new();
		bool called = false;
		registry.Register(Descriptor(), (_, _, _) => { called = true; return Client(() => "x"); }, (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));

		await Assert.ThrowsAsync<ChatException>(() => registry.CreateAdapter("plugin:local.plugin:mock")
			.CompleteAsync("", "", "model", "system", [new ChatMessageInput("user", "hi")]));
		await Assert.ThrowsAsync<ChatException>(() => registry.FetchModelsAsync("plugin:local.plugin:mock", "", ""));
		Assert.False(called);
	}

	[Fact]
	public async Task 默认地址与尾斜杠归一化后交给插件()
	{
		CustomChatProviderRegistry registry = new();
		string? url = null;
		registry.Register(new CustomChatProviderDescriptor("plugin:p:q", "P", "http://localhost:8080/v1/", false), (baseUrl, _, _) =>
		{
			url = baseUrl;
			return Client(() => "ok");
		}, (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));

		await registry.CreateAdapter("plugin:p:q")
			.CompleteAsync("", "", "m", "s", [new ChatMessageInput("user", "hi")]);

		Assert.Equal("http://localhost:8080/v1", url);
	}

	[Fact]
	public void ChatService共享注册表且插件提供商不经OpenAI解析()
	{
		using HttpClient http = new();
		ChatService chat = new(http, _database, _config);
		Assert.NotNull(chat.CustomProviders);
		Assert.False(LlmProviderExtensions.IsCustomProvider("openai"));
		Assert.True(LlmProviderExtensions.IsCustomProvider("plugin:a:b"));
		Assert.Throws<ChatException>(() => LlmProviderExtensions.ParseProvider("plugin:a:b"));
	}

	[Fact]
	public void 插件提供商配置在快照中保持原始ID且允许本地无密钥()
	{
		new AiSettingsStore(_config).UpdateChat(new AiChatSettingsPatch(
			Provider: "plugin:local.plugin:mock",
			BaseUrl: "",
			ApiKey: "",
			ApiKeySpecified: true,
			Model: "local-model"));
		AiChatSettings chat = new AiSettingsStore(_config).Read().Chat;

		Assert.Equal("plugin:local.plugin:mock", chat.ProviderId);
		Assert.Equal(LlmProvider.OpenAi, chat.Provider);
		Assert.True(chat.IsConfigured);
		Assert.Equal("plugin:local.plugin:mock", AiChatSettingsSnapshot.From(chat).Provider);
	}

	private sealed class ReplyClient(Func<string> reply) : IChatClient
	{
		public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
			Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, reply())));

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
			ChatOptions? options = null,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.Yield();
			yield return new ChatResponseUpdate(ChatRole.Assistant, reply());
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}
}
