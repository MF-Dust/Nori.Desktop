using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nori.Desktop.Memory;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeMemorySaveBarrierDisablesInputsUntilPendingSaveFinishes() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			window.Show(); await RefreshMemoryWindowForTest(window);
			MemorySettingDraft pending = new(1, _ => release.Task, () => { });
			NativeMemoryDrafts(window)["syntheticDelayedField"] = pending;
			pending.Set(2);
			Task<bool> flush = window.FlushPendingSavesAsync();
			Assert.False(flush.IsCompleted);
			Assert.False(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			release.SetResult();
			Assert.True(await flush);
			Assert.False(pending.Dirty);
			Assert.True(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			window.Hide(); window.Show();
			Assert.True(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			await window.PrepareShutdownAsync();
			Assert.False(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
		}
		finally { release.TrySetResult(); window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemoryFailedShutdownRestoresEditableDrafts() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		bool fail = true;
		try
		{
			window.Show(); await RefreshMemoryWindowForTest(window);
			MemorySettingDraft pending = new(1, _ => fail ? Task.FromException(new InvalidOperationException("合成保存失败")) : Task.CompletedTask, () => { });
			NativeMemoryDrafts(window)["syntheticFailedField"] = pending;
			pending.Set(2);
			await Assert.ThrowsAsync<InvalidOperationException>(window.PrepareShutdownAsync);
			Assert.True(pending.Dirty);
			Assert.Equal(2, pending.Value);
			Assert.True(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			fail = false;
			Assert.True(await window.FlushPendingSavesAsync());
			await window.PrepareShutdownAsync();
		}
		finally { fail = false; window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemorySaveBarrierKeepsDiscardDialogInteractiveAndRestoresEditorAfterCancel() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshMemoryWindowForTest(window);
			await window.OpenEditorAsync(null);
			Window editor = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
			editor.UpdateLayout();
			TextBox content = EditorControl<TextBox>(editor, "MemoryEditorContent");
			content.Text = "退出确认期间保留的合成草稿";
			Task<bool> flush = window.FlushPendingSavesAsync();
			Window confirmation = editor.OwnedWindows.Single(dialog => dialog.Name == "MemoryConfirmation");
			confirmation.UpdateLayout();
			Assert.False(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			Assert.False(Assert.IsAssignableFrom<Control>(editor.Content).IsEnabled);
			Button cancel = EditorControl<Button>(confirmation, "MemoryConfirmCancel");
			Assert.True(cancel.IsEffectivelyEnabled);
			cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(await flush);
			Assert.True(Assert.IsAssignableFrom<Control>(window.Content).IsEnabled);
			Assert.True(Assert.IsAssignableFrom<Control>(editor.Content).IsEnabled);
			Assert.Equal("退出确认期间保留的合成草稿", content.Text);
			Task<bool> discard = window.FlushPendingSavesAsync();
			RespondMemoryConfirmation(editor, true);
			Assert.True(await discard);
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.False(editor.IsVisible);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	private static Dictionary<string, MemorySettingDraft> NativeMemoryDrafts(MemoryWindow window) =>
		(Dictionary<string, MemorySettingDraft>)typeof(MemoryWindow).GetField("_drafts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
