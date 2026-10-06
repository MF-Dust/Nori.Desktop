using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Resources;
using Nori.Core.Telemetry;
using Nori.Desktop.Chat;
using Nori.Desktop.FirstRun;
using Nori.Desktop.Live2D;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class AvailableUxTelemetry : ITelemetry
	{
		public bool IsAvailable => true;
		public bool IsEnabled => false;
		public void Configure(bool enabled) { }
		public void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false, IReadOnlyDictionary<string, string>? tags = null) { }
		public ITelemetryTransaction StartTransaction(string operation) => NoopTelemetry.Instance.StartTransaction(operation);
		public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;
		public void Dispose() { }
	}

	[Fact]
	public Task 首次运行诊断选择在返回和语言重建后保留() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._services.Telemetry = new AvailableUxTelemetry();
		FirstRunSteps steps = new(fixture._services, _ => { }, () => { }, _ => Task.FromResult<string?>(null), CancellationToken.None);
		Control ready = steps.Build(WizardStep.Ready, false);
		CheckBox checkbox = Assert.Single(ready.GetLogicalDescendants().OfType<CheckBox>());
		Assert.True(checkbox.IsChecked);
		checkbox.IsChecked = false;
		steps.Build(WizardStep.Ai, false);
		ready = steps.Build(WizardStep.Ready, true);
		Assert.False(steps.TelemetryEnabled);
		Assert.False(Assert.Single(ready.GetLogicalDescendants().OfType<CheckBox>()).IsChecked);
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("en-US", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 1920, 1080)]
	public Task 设置关闭失败会显示非法字段所在页(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new(fixture._services) { Width = width, Height = height };
		try
		{
			window.Show();
			SettingsViewModel vm = Assert.IsType<SettingsViewModel>(window.DataContext);
			await vm.RefreshSnapshotAsync();
			SettingsPageBase ai = vm.Groups.SelectMany(group => group.Pages).Single(page => page.Key == "ai").Page;
			SettingsFieldViewModel dimensions = ai.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "embeddingDimensions");
			dimensions.Text = "0";
			vm.Navigate("general");
			vm.SearchText = "general";
			window.Close();
			await WaitUntilAsync(() => vm.CurrentPage == ai && ai.ErrorMessage.Length > 0);
			Assert.True(window.IsVisible);
			Assert.Empty(vm.SearchText);
			Assert.NotEmpty(ai.ErrorMessage);
			window.UpdateLayout();
			TextBlock banner = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "SettingsSaveError");
			Assert.True(banner.IsVisible);
			Assert.Contains(ai.ErrorMessage, banner.Text);
			Assert.True(banner.Bounds.Height > 0);
			var origin = banner.TranslatePoint(default, window);
			Assert.NotNull(origin);
			Assert.InRange(origin.Value.Y + banner.Bounds.Height, 0, window.ClientSize.Height);
			CaptureUi(window, $"ux-settings-save-error-{width}x{height}-{language}");
			dimensions.Text = "";
			Assert.True(await vm.FlushPendingSavesAsync());
			window.Close();
			await WaitUntilAsync(() => !window.IsVisible);
		}
		finally { window.AllowClose = true; window.Close(); SettingsLocalization.SetLanguage("zh-CN"); }
	});

	[Fact]
	public Task 转写失败后聊天不再显示录音中() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using NativeChatService service = new(fixture._services, new Window());
		using ChatView view = new(service, (command, _, _) => command == "stt_stop"
			? Task.FromException<JsonElement>(new InvalidOperationException("转写网络失败"))
			: Task.FromResult(JsonSerializer.SerializeToElement(new { })));
		await view.ToggleVoiceAsync();
		Assert.Equal("recording", view.VoiceState);
		await Assert.ThrowsAsync<InvalidOperationException>(view.StopRecordingAsync);
		Assert.Equal("idle", view.VoiceState);
	});

	[NativeGlAssetsTheory]
	[InlineData(false, "arg-nori", 1)]
	[InlineData(false, "nori", 0)]
	[InlineData(true, "arg-nori", 0)]
	public Task 仅覆盖当前模型时请求热重载且安全模式不自动加载(bool safeMode, string importedId, int expectedRequests) => WithSettingsUiAsync(async () =>
	{
		PreparedModel prepared = await Task.Run(() => ModelPreparation.PrepareAsync("arg-nori",
			Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture("arg-nori", "ARGNori.model3.json"))!, 1, CancellationToken.None));
		// 保持 Avalonia 图片解码服务存活，EGL 操作放到同一后台线程。
		await Task.Run(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: safeMode);
			PetRuntime pet = new(fixture._services, previewMode: true);
			fixture._services.PetRuntime = pet;
			// EGL 上下文创建后保持同一线程；命令后台导入，不在 UI 线程阻塞等待。
			using AngleTestContext context = new(2);
			context.MakeCurrent(0);
			try
			{
				pet.OnGlInit(context.CreateApi());
				InstallOperation(pet, prepared!);
				pet.RunSynchronized(() => pet.RenderFrame(0.016f, 720, 480));
				NativeModelHost original = Assert.IsType<NativeModelHost>(pet.CurrentModel);
				int requests = 0;
				pet.ModelLoadRequested += () => requests++;
				string source = Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture(importedId,
					importedId == "arg-nori" ? "ARGNori.model3.json" : "Nori.model3.json"))!;
				fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "model_import_local",
					Args(new { sourceKind = "folder", resourceType = "live2d", filePath = source })).GetAwaiter().GetResult();
				Assert.Equal(expectedRequests, requests);
				Assert.True(fixture._services.Resources.IsInstalled(ResourceType.Live2D, importedId));
				if (expectedRequests > 0)
				{
					ModelLoadOperation pending = Assert.IsType<ModelLoadOperation>(typeof(PetRuntime)
						.GetField("_pendingModelLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet));
					pending.PreparationTask.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
					pet.RunSynchronized(() => pet.RenderFrame(0.016f, 720, 480));
					Assert.True(pending.Completion.IsCompletedSuccessfully, pending.Completion.Exception?.ToString() ?? pet.LastModelLoadError);
					Assert.NotSame(original, pet.CurrentModel);
					Assert.True(original.Model.IsDisposed);
				}
				else Assert.Same(original, pet.CurrentModel);
				Assert.Equal(0, context.GetError());
			}
			finally { pet.RunSynchronized(pet.OnGlDeinit); }
		});
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task 记忆归档恢复允许保留未保存草稿(bool archived) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		var item = fixture._services.Memory.AddAggregate("manual", "保存的原文");
		if (archived) fixture._services.Memory.Archive(item.Id);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show();
			await window.OpenEditorAsync(item.Id);
			Window editor = Assert.IsType<Window>(typeof(MemoryWindow).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
			TextBox content = editor.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "MemoryEditorContent");
			content.Text = "未保存的修改";
			// 归档/恢复按钮位于页脚，先出现的确认必须是未保存提醒，而非状态操作。
			Button action = editor.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, archived ? "恢复" : "归档此记忆"));
			action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => editor.OwnedWindows.Count > 0);
			Window confirmation = Assert.Single(editor.OwnedWindows);
			Assert.Contains(confirmation.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, "继续编辑"));
			CaptureUi(confirmation, $"ux-memory-unsaved-{(archived ? "restore" : "archive")}");
			confirmation.Close(false);
			await Task.Yield();
			Assert.True(editor.IsVisible);
			Assert.Equal("未保存的修改", content.Text);
			Assert.Equal("保存的原文", fixture._services.Memory.Get(item.Id)!.Content);
			Assert.Equal(archived ? "archived" : "active", fixture._services.Memory.Get(item.Id)!.Status);
			// 明确选择放弃修改后，才进入原有的归档/恢复确认。
			action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => editor.OwnedWindows.Count > 0);
			Assert.Single(editor.OwnedWindows).Close(true);
			await WaitUntilAsync(() => editor.OwnedWindows.Count > 0);
			Window statusConfirmation = Assert.Single(editor.OwnedWindows);
			Assert.DoesNotContain(statusConfirmation.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, "继续编辑"));
			statusConfirmation.Close(true);
			await WaitUntilAsync(() => !editor.IsVisible);
			Assert.Equal(archived ? "active" : "archived", fixture._services.Memory.Get(item.Id)!.Status);
		}
		finally { window.AllowClose = true; window.Close(); }
	});
}
