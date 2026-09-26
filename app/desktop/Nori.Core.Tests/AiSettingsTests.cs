using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;
using Nori.Core.Tests.TestSupport;
using System.Text.Json;

namespace Nori.Core.Tests;

public sealed class AiSettingsTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-ai-settings");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly AiSettingsStore _settings;

	public AiSettingsTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_settings = new AiSettingsStore(_config);
	}

	[Fact]
	public void Embedding不会从聊天配置回退()
	{
		_config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("https://chat.example/v1"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text("chat-secret"));
		_config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("chat-model"));

		AiProviderSettings settings = _settings.Read();

		Assert.Equal("https://chat.example/v1", settings.Chat.BaseUrl);
		Assert.Equal("", settings.Embedding.BaseUrl);
		Assert.Equal("", settings.Embedding.ApiKey);
		Assert.False(settings.Embedding.IsConfigured);
	}

	[Fact]
	public void Embedding可以使用独立端点且密钥可选()
	{
		_settings.UpdateEmbedding(new AiEmbeddingSettingsPatch(
			BaseUrl: "http://localhost:11434/v1",
			Model: "nomic-embed-text"));

		AiEmbeddingSettings embedding = _settings.Read().Embedding;

		Assert.Equal("http://localhost:11434/v1", embedding.BaseUrl);
		Assert.Equal("nomic-embed-text", embedding.Model);
		Assert.Empty(embedding.ApiKey);
		Assert.True(embedding.IsConfigured);
		Assert.False(_config.Exists(AiSettingsStore.KeyEmbeddingApiKey));
	}

	[Fact]
	public void 更新Embedding不会覆盖聊天配置且密钥仍受保护()
	{
		_settings.UpdateChat(new AiChatSettingsPatch(
			BaseUrl: "https://chat.example/v1",
			ApiKey: "chat-secret",
			Model: "chat-model",
			ApiKeySpecified: true));
		_settings.UpdateEmbedding(new AiEmbeddingSettingsPatch(
			BaseUrl: "https://embed.example/v1",
			ApiKey: "embedding-secret",
			Model: "embed-model",
			ApiKeySpecified: true));

		AiProviderSettings settings = _settings.Read();

		Assert.Equal("chat-secret", settings.Chat.ApiKey);
		Assert.Equal("embedding-secret", settings.Embedding.ApiKey);
		Assert.StartsWith(SecretProtector.Prefix, _config.RawValue(AiSettingsStore.KeyEmbeddingApiKey), StringComparison.Ordinal);
		Assert.StartsWith(SecretProtector.Prefix, _config.RawValue(AiSettingsStore.KeyLlmApiKey), StringComparison.Ordinal);
	}

	[Fact]
	public void 完整AI配置只执行一次指定键批量读取且快照不含密钥()
	{
		_settings.UpdateChat(new AiChatSettingsPatch(
			BaseUrl: "https://chat.example/v1",
			ApiKey: "chat-secret-value",
			Model: "chat-model",
			ApiKeySpecified: true));
		_settings.UpdateEmbedding(new AiEmbeddingSettingsPatch(
			BaseUrl: "https://embed.example/v1",
			ApiKey: "embedding-secret-value",
			Model: "embed-model",
			ApiKeySpecified: true));

		int queryCount = 0;
		_config.ReadQueryExecuted = () => queryCount++;
		AiProviderSettings settings = _settings.Read();

		Assert.Equal(1, queryCount);
		Assert.Equal("chat-secret-value", settings.Chat.ApiKey);
		Assert.Equal("embedding-secret-value", settings.Embedding.ApiKey);

		string chatSnapshot = JsonSerializer.Serialize(AiChatSettingsSnapshot.From(settings.Chat));
		string embeddingSnapshot = JsonSerializer.Serialize(AiEmbeddingSettingsSnapshot.From(settings.Embedding));
		Assert.Contains("\"hasApiKey\":true", chatSnapshot, StringComparison.Ordinal);
		Assert.Contains("\"hasApiKey\":true", embeddingSnapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("chat-secret-value", chatSnapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("embedding-secret-value", embeddingSnapshot, StringComparison.Ordinal);
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}
}
