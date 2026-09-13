using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Memory;
using Nori.Core.Configuration;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeMemoryEditorLoadsProvenanceAndPersistsEveryEditableField() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeMemoryVisualData(fixture);
		MemoryItem item = fixture._services.Memory.GetAll(100).First(memory => memory.Status == "active");
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("memories");
			await RefreshMemoryWindowForTest(window);
			await window.OpenEditorAsync(item.Id);
			Window editor = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
			editor.UpdateLayout();
			string labels = string.Join("\n", editor.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
			Assert.Contains("合成来源消息", labels);
			Assert.Contains("#1", labels);
			Assert.Contains("user", labels);
			Button save = EditorControl<Button>(editor, "MemoryEditorSave");
			Assert.False(save.IsEnabled);
			EditorControl<TextBox>(editor, "MemoryEditorContent").Text = "修改后的完整正文";
			EditorControl<TextBox>(editor, "MemoryEditorCanonical").Text = "修改后的规范摘要";
			EditorControl<TextBox>(editor, "MemoryEditorPersona").Text = "修改后的 Nori 视角摘要";
			EditorControl<TextBox>(editor, "MemoryEditorTags").Text = "测试, 标签";
			ComboBox kind = EditorControl<ComboBox>(editor, "MemoryEditorKind");
			kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().Single(choice => choice.Tag as string == "identity");
			EditorControl<NumericUpDown>(editor, "MemoryEditorImportance").Value = 0.65m;
			EditorControl<NumericUpDown>(editor, "MemoryEditorConfidence").Value = 0.95m;
			TextBox draftContent = EditorControl<TextBox>(editor, "MemoryEditorContent");
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			fixture._runtime.InvalidateSnapshot("general");
			await RefreshMemoryWindowForTest(window);
			await WaitUntilAsync(() => editor.Title?.StartsWith("Memory details", StringComparison.Ordinal) == true);
			Assert.Equal(Nori.Desktop.Memory.MemoryResources.Get("detail.title", "en-US") + " #" + item.Id, editor.Title);
			Assert.Same(draftContent, EditorControl<TextBox>(editor, "MemoryEditorContent"));
			Assert.Equal("修改后的完整正文", draftContent.Text);
			Assert.Contains(Nori.Desktop.Memory.MemoryResources.Get("detail.neverAccessed", "en-US"), string.Join("\n", editor.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text)));
			Assert.True(save.IsEnabled);
			save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(await window.FlushPendingSavesAsync());
			MemoryItem updated = Assert.IsType<MemoryItem>(fixture._services.Memory.Get(item.Id));
			Assert.Equal("修改后的完整正文", updated.Content);
			Assert.Equal("修改后的规范摘要", updated.CanonicalSummary);
			Assert.Equal("修改后的 Nori 视角摘要", updated.PersonaSummary);
			Assert.Equal("测试, 标签", updated.Tags);
			Assert.Equal("identity", updated.Kind);
			Assert.Equal(0.65, updated.Importance);
			Assert.Equal(0.95, updated.Confidence);
			if (editor.IsVisible) editor.Close();
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemoryEditorRequiresDiscardConfirmationAndKeepsCancelledDraft() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshMemoryWindowForTest(window);
			await window.OpenEditorAsync(null);
			Window editor = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
			editor.UpdateLayout();
			Assert.False(EditorControl<Button>(editor, "MemoryEditorSave").IsEnabled);
			TextBox content = EditorControl<TextBox>(editor, "MemoryEditorContent");
			content.Text = "必须保留的未保存草稿";
			editor.Close();
			Window confirm = editor.OwnedWindows.Single(dialog => dialog.Name == "MemoryConfirmation");
			confirm.UpdateLayout();
			EditorControl<Button>(confirm, "MemoryConfirmCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.True(editor.IsVisible);
			Assert.Equal("必须保留的未保存草稿", content.Text);
			editor.Close();
			confirm = editor.OwnedWindows.Single(dialog => dialog.Name == "MemoryConfirmation");
			confirm.UpdateLayout();
			EditorControl<Button>(confirm, "MemoryConfirmAccept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.False(editor.IsVisible);
			Assert.Empty(fixture._services.Memory.GetAll(100));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemoryArchiveAndDeleteOnlyRunAfterConfirmation() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryItem item = fixture._services.Memory.AddAggregate("manual", "合成操作确认记忆", source: "manual");
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshMemoryWindowForTest(window);
			Task<bool> cancelled = window.ChangeMemoryAsync(item.Id, "archive");
			RespondMemoryConfirmation(window, false);
			Assert.False(await cancelled);
			Assert.Equal("active", fixture._services.Memory.Get(item.Id)!.Status);
			foreach ((string operation, string status) in new[] {("archive", "archived"), ("restore", "active")})
			{
				Task<bool> change = window.ChangeMemoryAsync(item.Id, operation);
				RespondMemoryConfirmation(window, true);
				Assert.True(await change);
				Assert.Equal(status, fixture._services.Memory.Get(item.Id)!.Status);
			}
			Task<bool> delete = window.ChangeMemoryAsync(item.Id, "delete");
			RespondMemoryConfirmation(window, true);
			Assert.True(await delete);
			Assert.Null(fixture._services.Memory.Get(item.Id));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	private static T EditorControl<T>(Window window, string name) where T : Control =>
		window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

	private static void RespondMemoryConfirmation(Window owner, bool accepted)
	{
		Window confirm = owner.OwnedWindows.Single(dialog => dialog.Name == "MemoryConfirmation");
		confirm.UpdateLayout();
		EditorControl<Button>(confirm, accepted ? "MemoryConfirmAccept" : "MemoryConfirmCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
	}
}
