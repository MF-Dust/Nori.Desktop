using System.Text.Json;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Tests;

/// <summary>复杂原生设置页的 JSON 契约和纯逻辑测试。</summary>
[Collection("Native settings")]
public sealed class SettingsPagesTests
{
	[Fact]
	public void SettingsJsonReadsCaseInsensitiveCollections()
	{
		using JsonDocument document = JsonDocument.Parse("{\"SERVERS\":[{\"ID\":\"one\"}],\"enabled\":\"true\",\"count\":\"12\"}");
		JsonElement root = document.RootElement;

		Assert.True(SettingsJson.Bool(root, "enabled"));
		Assert.Equal(12, SettingsJson.Int(root, "count"));
		Assert.Single(SettingsJson.RootArray(root));
		Assert.Equal("one", SettingsJson.String(SettingsJson.RootArray(root).Single(), "id"));
	}

	[Fact]
	public void SettingsJsonKeepsRawToolSchemaAndRejectsInvalidJson()
	{
		JsonElement? parsed = SettingsJson.TryParse("{\"type\":\"object\",\"properties\":{}} ");
		Assert.True(parsed.HasValue);
		Assert.Equal("object", SettingsJson.String(parsed.Value, "type"));
		Assert.Null(SettingsJson.TryParse("{invalid"));
	}

	[Fact]
	public void SkillDraftUsesStableCustomDefaults()
	{
		SkillDraft draft = SkillsSettingsViewModel.NewDraft("tester");

		Assert.StartsWith("skill_custom_", draft.Id, StringComparison.Ordinal);
		Assert.Equal("tester", draft.Author);
		Assert.Equal("custom", draft.Source);
		Assert.True(draft.Enabled);
	}

	[Fact]
	public void PluginCapabilityLabelFailsClosed()
	{
		PluginItem plugin = new(
			"demo",
			"Demo",
			"",
			"1.0.0",
			"tester",
			null,
			null,
			null,
			"installed",
			true,
			["ui.webview", "filesystem"],
			[],
			[
				new PluginCapabilityItem("ui.webview", true, true, true),
				new PluginCapabilityItem("filesystem", true, false, true),
			],
			null,
			null,
			false,
			null);

		Assert.Equal("granted", PluginsSettingsViewModel.CapabilityLabel(plugin, "ui.webview"));
		Assert.Equal("unavailable", PluginsSettingsViewModel.CapabilityLabel(plugin, "filesystem"));
		Assert.Equal("undeclared", PluginsSettingsViewModel.CapabilityLabel(plugin, "network"));
	}

	[Fact]
	public void NativeResourcesExposeBothLanguages()
	{
		SettingsLocalization.SetLanguage("zh-CN");
		string chinese = NativeSettingsResources.Get("skills.installed");
		SettingsLocalization.SetLanguage("en-US");
		string english = NativeSettingsResources.Get("skills.installed");
		SettingsLocalization.SetLanguage("zh-CN");

		Assert.Equal("已安装", chinese);
		Assert.Equal("Installed", english);
	}
}
