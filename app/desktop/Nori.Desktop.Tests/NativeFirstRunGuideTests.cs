using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Desktop.FirstRun;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 首次运行向导的内容与排版。
///
/// 用户反馈装好之后不知道怎么用，向导因此补了使用方法一步和各步的说明。这里守住两件事：
/// 每一步在默认窗口里一屏放得下（不用滚动就能看到全部说明），以及说明对应真实入口。
/// </summary>
public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("zh-CN")]
	[InlineData("en-US")]
	public Task 首次运行每一步在默认窗口内一屏放得下(string language) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		fixture.InstallKnownModel("arg-nori");
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
		try
		{
			window.Show();
			Button next = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "FirstRunNext");
			for (int index = 0; index < FirstRunWizard.Order.Count; index++)
			{
				WizardStep step = FirstRunWizard.Order[index];
				window.UpdateLayout();
				await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
				window.UpdateLayout();
				ScrollViewer stage = window.GetLogicalDescendants().OfType<ScrollViewer>()
					.Single(item => item.Name == FirstRunSteps.StageName);
				CaptureUi(window, $"first-run-{language}-{index + 1}-{step.ToString().ToLowerInvariant()}");
				Assert.True(stage.Extent.Height <= stage.Viewport.Height + 1,
					$"{language} {step} 需要滚动：内容 {stage.Extent.Height:F0} > 可视 {stage.Viewport.Height:F0}");
				Assert.True(stage.Extent.Width <= stage.Viewport.Width + 1,
					$"{language} {step} 横向溢出：内容 {stage.Extent.Width:F0} > 可视 {stage.Viewport.Width:F0}");
				if (index == FirstRunWizard.Order.Count - 1) break;
				Assert.True(next.IsEnabled, $"{language} {step} 无法前进");
				next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			}
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
		}
	});

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, true)]
	[InlineData(true, false, false)]
	[InlineData(true, true, false)]
	[InlineData(true, false, true)]
	public void 首次运行使用方法覆盖主要入口并按平台改写(bool english, bool mac, bool windows)
	{
		(string Icon, string Title, string Body)[] cards = FirstRunSteps.GuideCards(english, mac, windows);

		Assert.Equal(["chat", "move", "mouse", "tray", "home", "shield"], cards.Select(card => card.Icon));
		Assert.All(cards, card => Assert.False(string.IsNullOrWhiteSpace(card.Title)));
		Assert.Contains(mac ? "⌘K" : "Ctrl+K", cards[0].Body, StringComparison.Ordinal);
		Assert.DoesNotContain(mac ? "Ctrl+K" : "⌘K", cards[0].Body, StringComparison.Ordinal);
		Assert.Equal(mac ? english ? "Menu bar icon" : "菜单栏图标" : english ? "Tray icon" : "托盘图标", cards[3].Title);
		Assert.Equal(windows, cards[3].Body.Contains('^'));
	}

	[Fact]
	public Task 首次运行使用方法一步画出六张卡片() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		FirstRunSteps steps = NewSteps(fixture);
		Control guide = steps.Build(WizardStep.Guide, false);

		Grid grid = guide.GetLogicalDescendants().OfType<Grid>().Single(item => item.Name == "FirstRunGuide");
		Assert.Equal(6, grid.Children.Count);
		Assert.Equal(3, grid.RowDefinitions.Count);
		Assert.Empty(steps.Gate);
		Assert.Equal("使用方法", FirstRunSteps.Title(WizardStep.Guide, false));
		Assert.Equal("How to use", FirstRunSteps.Title(WizardStep.Guide, true));
		return Task.CompletedTask;
	});

	[Fact]
	public Task 首次运行形象卡片显示名字和立绘而非内部ID() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.InstallKnownModel("arg-nori");
		using FirstRunSteps steps = NewSteps(fixture);
		Control model = steps.Build(WizardStep.Model, false);

		List<string> texts = [.. model.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "")];
		Assert.Contains("ARG Nori", texts);
		Assert.Contains("Nori", texts);
		Assert.DoesNotContain("arg-nori", texts);
		Assert.Contains(texts, text => text.Contains(".model3.json", StringComparison.Ordinal));
		Assert.Equal(2, model.GetLogicalDescendants().OfType<Image>().Count());
		Assert.Equal("arg-nori", steps.SelectedModel);
		Assert.Empty(steps.Gate);

		// 关窗释放立绘之后再构建也不能抛：只画占位底色。
		steps.Dispose();
		Assert.Empty(steps.Build(WizardStep.Model, false).GetLogicalDescendants().OfType<Image>());
		return Task.CompletedTask;
	});

	[Fact]
	public Task 首次运行模型服务一步显示协议名并随协议切换说明() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		FirstRunSteps steps = NewSteps(fixture);
		Control ai = steps.Build(WizardStep.Ai, false);

		ComboBox provider = ai.GetLogicalDescendants().OfType<ComboBox>().Single(item => AutomationProperties.GetName(item) == "协议");
		Assert.Equal(AiDraftDefaults.Providers.Select(AiDraftDefaults.DisplayName), provider.ItemsSource!.Cast<string>());
		Assert.Equal(0, provider.SelectedIndex);
		ComboBox picker = ai.GetLogicalDescendants().OfType<ComboBox>().Single(item => item.Name == "FirstRunModelPicker");
		Assert.False(picker.IsVisible);

		Border help = ai.GetLogicalDescendants().OfType<Border>().Single(item => item.Name == "FirstRunAiHelp");
		provider.SelectedIndex = AiDraftDefaults.Providers.ToList().IndexOf(AiDraftDefaults.Anthropic);
		List<string> helpTexts = [.. help.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "")];
		Assert.Contains("Anthropic", helpTexts);
		Assert.Contains(helpTexts, text => text.Contains("sk-ant-", StringComparison.Ordinal));
		Assert.Contains(helpTexts, text => text.Contains("不会同步到云端", StringComparison.Ordinal));
		return Task.CompletedTask;
	});

	[Fact]
	public Task 首次运行就绪页在未配置模型服务时提示后果() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.InstallKnownModel("arg-nori");
		FirstRunSteps steps = NewSteps(fixture);
		steps.Build(WizardStep.Model, false);

		Control ready = steps.Build(WizardStep.Ready, false);
		Assert.Single(ready.GetLogicalDescendants().OfType<TextBlock>(), text => text.Name == "FirstRunAiMissing");
		List<string> texts = [.. ready.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "")];
		Assert.Contains("ARG Nori", texts);
		Assert.Contains("未配置", texts);
		Assert.Contains(texts, text => text.Contains("安全模式下不会自动唤出", StringComparison.Ordinal));

		// 输入框挂进窗口再改文字，与真实向导里用户敲键的路径一致。
		Control ai = steps.Build(WizardStep.Ai, false);
		Window host = new() {Content = ai, Width = 800, Height = 560};
		try
		{
			host.Show();
			TextBox[] boxes = [.. ai.GetLogicalDescendants().OfType<TextBox>()];
			boxes.Single(box => AutomationProperties.GetName(box) == "密钥").Text = "sk-first-run";
			boxes.Single(box => AutomationProperties.GetName(box) == "模型").Text = "gpt-first-run";
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.True(await steps.SaveAiDraftAsync());
			Assert.True(steps.AiSaved);
		}
		finally { host.Close(); }

		ready = steps.Build(WizardStep.Ready, false);
		Assert.DoesNotContain(ready.GetLogicalDescendants().OfType<TextBlock>(), text => text.Name == "FirstRunAiMissing");
		Assert.Contains(ready.GetLogicalDescendants().OfType<TextBlock>(),
			text => text.Text == "OpenAI Chat Completions · gpt-first-run");
	});

	private static FirstRunSteps NewSteps(BridgeCommandsTests fixture) =>
		new(fixture._services, _ => { }, () => { }, _ => Task.FromResult<string?>(null), CancellationToken.None);
}
