using System.Text.Json;
using Avalonia.Controls;
using Nori.Core.Chat;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeAiProvidersIncludeActivePluginMetadata() => WithSettingsUiAsync(() =>
	{
		using SettingsService service = new(_services, new Window());
		using AiSettingsPage page = new(service);
		SettingsFieldViewModel provider = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "provider");
		using JsonDocument snapshot = JsonDocument.Parse("""
			{"ai":{"chat":{"provider":"plugin:local.plugin:mock","baseUrl":"","model":"local-model","configured":true},
			"providers":[{"id":"plugin:local.plugin:mock","name":"本地测试","defaultBaseUrl":"","requiresApiKey":false}]}}
			""");

		page.ApplySnapshot(snapshot.RootElement);

		SettingsOption option = provider.Options.Single(item => item.Value == "plugin:local.plugin:mock");
		Assert.Equal("本地测试", option.Text.Chinese);
		Assert.Equal("plugin:local.plugin:mock", provider.Selected);
		Assert.Single(provider.Options, item => item.Value == "openai");

		// 插件撤销后元数据消失，但配置中的 ID 仍显示为不可用，不会被静默改成 OpenAI。
		using JsonDocument revoked = JsonDocument.Parse("""
			{"ai":{"chat":{"provider":"plugin:local.plugin:mock","baseUrl":"","model":"local-model","configured":true},"providers":[]}}
			""");
		page.ApplySnapshot(revoked.RootElement);
		Assert.Equal("plugin:local.plugin:mock", provider.Selected);
		Assert.Contains(provider.Options, item => item.Value == "plugin:local.plugin:mock");
		Assert.Equal("", AiSettingsPage.DefaultBaseUrl("plugin:local.plugin:mock", "https://api.openai.com/v1",
			[new CustomChatProviderDescriptor("plugin:local.plugin:mock", "本地", "", false)]));
		Assert.Equal("http://localhost:9000/v1", AiSettingsPage.DefaultBaseUrl("plugin:local.plugin:mock", "http://localhost:9000/v1",
			[new CustomChatProviderDescriptor("plugin:local.plugin:mock", "本地", "", false)]));
		// 从插件切回内置协议时，插件默认地址视为已知默认值，不能当成自定义代理保留。
		CustomChatProviderDescriptor local = new("plugin:local.plugin:lan", "LAN", "http://127.0.0.1:1234/v1", false);
		Assert.Equal("https://api.openai.com/v1", AiSettingsPage.DefaultBaseUrl("openai", "http://127.0.0.1:1234/v1", [local]));
		Assert.Equal("http://localhost:9000/v1", AiSettingsPage.DefaultBaseUrl("openai", "http://localhost:9000/v1", [local]));
		return Task.CompletedTask;
	});
}
