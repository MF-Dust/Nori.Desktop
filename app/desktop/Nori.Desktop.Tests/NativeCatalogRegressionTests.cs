using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Mcp;
using Nori.Core.Skills;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public Task 文件导入保留超过四千字符的完整内容(bool skill) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		string content = new('x', 5000);
		string document = skill
			? $"---\nname: long-skill\ndescription: regression\n---\n{content}"
			: JsonSerializer.Serialize(new {mcpServers = new {demo = new {command = "npx", env = new {LONG_VALUE = content}}}});
		Window owner = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, owner);
		SkillsSettingsViewModel skills = new(service);
		McpSettingsViewModel mcp = new(service);
		owner.Show();
		Task importing = skill
			? NativeCatalogImportDialogs.ShowSkillImportAsync(owner, skills, (_, _, _) => Task.FromResult<string?>(document))
			: NativeCatalogImportDialogs.ShowMcpImportAsync(owner, mcp, (_, _, _) => Task.FromResult<string?>(document));
		Window dialog = Assert.Single(owner.OwnedWindows);
		try
		{
			Button FindButton(string key) => dialog.GetLogicalDescendants().OfType<Button>()
				.Single(button => (string?)button.Content == NativeSettingsResources.Get(key));
			TextBox paste = dialog.GetLogicalDescendants().OfType<TextBox>().First();
			FindButton("skills.pickFile").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => !string.IsNullOrEmpty(paste.Text));
			Assert.Equal(document, paste.Text);
			FindButton("skills.preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Button confirm = FindButton("common.confirm");
			await WaitUntilAsync(() => confirm.IsEnabled);
			confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await importing;
			if (skill)
			{
				Assert.Equal(content, Assert.Single(fixture._runtime.Skills.GetInstalled(), item => item.Name == "long-skill").Instructions);
			}
			else
			{
				string id = Assert.Single(fixture._services.Mcp.GetServerConfigs()).Id;
				Assert.Equal(content, new McpEnvironmentStore(fixture._config).Read(id)["LONG_VALUE"]);
			}
		}
		finally
		{
			dialog.Close();
			await importing;
			owner.Close();
		}
	});

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 编辑停用技能后卡片立即刷新(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		Window window = new() {Width = width, Height = height};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		SettingsPagePresenter presenter = new();
		SkillDraft draft = SkillsSettingsViewModel.NewDraft("author") with {Name = "旧名称", Instructions = "正文", Enabled = false};
		fixture._runtime.Skills.SaveCustom(new SkillRecord
		{
			Id = draft.Id, Name = draft.Name, Instructions = draft.Instructions, Enabled = false, Source = "custom",
		});
		try
		{
			window.Show();
			await ShowPageAsync(window, presenter, viewModel, "skills");
			SkillsSettingsViewModel skills = Assert.IsType<SkillsSettingsViewModel>(Assert.IsAssignableFrom<NativeSettingsPageBase>(viewModel.CurrentPage).ComplexViewModel);
			Assert.Contains(presenter.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "旧名称");
			int promptCharacters = skills.PromptCharacters;
			await skills.SaveCustomAsync(draft with {Name = "新名称", Description = "新描述", Category = "coding", Tags = ["新标签"]});
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.Equal(promptCharacters, skills.PromptCharacters);
			Assert.DoesNotContain(presenter.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "旧名称");
			foreach (string text in new[] {"新名称", "新描述", "新标签"})
				Assert.Contains(presenter.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == text);
			skills.SearchText = "新名称";
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			CaptureUi(window, $"skills-edited-{language}-{width}x{height}");
		}
		finally
		{
			presenter.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});
}
