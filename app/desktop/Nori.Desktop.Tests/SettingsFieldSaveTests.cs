using System.Text.Json;
using Avalonia.Controls;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class PendingSavePage : SettingsPageBase
	{
		public PendingSavePage(SettingsService service, Func<object?, CancellationToken, Task<JsonElement>> save, bool secret = false)
			: base(service, "pending-save", "core", new("保存测试", "Save test"), new("", ""), CancellationToken.None)
		{
			Field = AddField(AddSection(new("测试字段", "Test field")), "value", new("值", "Value"), new("", ""),
				secret ? SettingsEditorKind.Password : SettingsEditorKind.Text,
				snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "value"),
				string.Empty, save, secret: secret);
		}

		public SettingsFieldViewModel Field { get; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task NativeFieldOlderSavePreservesNewerEdit(bool secret) => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseLatest = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> saved = [];
		using PendingSavePage page = new(service, async (value, _) =>
		{
			saved.Add((string)value!);
			if (saved.Count == 1)
			{
				started.TrySetResult(true);
				// 模拟请求已经发出后无法撤销的异步持久化。
				await release.Task;
			}
			else await releaseLatest.Task;
			return default;
		}, secret);
		page.Field.Text = "first-value";
		Task<bool> first = page.Field.SaveNowAsync();
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			page.Field.Text = "second-value";
			page.ApplySnapshot(JsonSerializer.SerializeToElement(new {value = "old-snapshot"}));
			release.SetResult(true);
			Assert.False(await first);
			Assert.Equal("second-value", page.Field.Text);
			Assert.True(page.Field.IsDirty);
			releaseLatest.SetResult(true);
			Assert.True(await page.FlushPendingSavesAsync());
			Assert.Equal(["first-value", "second-value"], saved);
			Assert.False(page.Field.IsDirty);
			Assert.Equal(secret ? string.Empty : "second-value", page.Field.Text);
		}
		finally
		{
			release.TrySetResult(true);
			releaseLatest.TrySetResult(true);
			await first;
			await page.FlushPendingSavesAsync();
		}
	});

	[Fact]
	public Task NativeFieldFlushPersistsEditMadeWhileSaveIsPending() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> saved = [];
		using PendingSavePage page = new(service, async (value, _) =>
		{
			saved.Add((string)value!);
			if (saved.Count == 1)
			{
				started.TrySetResult(true);
				await release.Task;
			}
			return default;
		});
		page.Field.Text = "first-value";
		Task<bool> flush = page.FlushPendingSavesAsync();
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			page.Field.Text = "latest-value";
			release.SetResult(true);
			Assert.True(await flush);
			Assert.Equal(["first-value", "latest-value"], saved);
			Assert.False(page.Field.IsDirty);
			Assert.Equal("latest-value", page.Field.Text);
		}
		finally
		{
			release.TrySetResult(true);
			await flush;
			await page.FlushPendingSavesAsync();
		}
	});

	[Fact]
	public Task NativeFieldDebounceCoalescesEditsAndFlushDoesNotRepeatSave() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> saved = [];
		using PendingSavePage page = new(service, (value, _) =>
		{
			saved.Add((string)value!);
			completed.TrySetResult(true);
			return Task.FromResult(default(JsonElement));
		});
		page.Field.Text = "a";
		page.Field.Text = "ab";
		page.Field.Text = "abc";
		try
		{
			await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(await page.FlushPendingSavesAsync());
			Assert.Equal(["abc"], saved);
			Assert.False(page.Field.IsDirty);
		}
		finally { await page.FlushPendingSavesAsync(); }
	});

	[Fact]
	public Task NativeFieldFailedSaveKeepsDraftAndCanRetry() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		int attempts = 0;
		using PendingSavePage page = new(service, (_, _) =>
		{
			if (++attempts == 1) throw new InvalidOperationException("保存失败");
			return Task.FromResult(default(JsonElement));
		});
		page.Field.Text = "draft";
		Assert.False(await page.FlushPendingSavesAsync());
		Assert.True(page.Field.IsDirty);
		Assert.Equal("保存失败", page.Field.ErrorText);
		page.ApplySnapshot(JsonSerializer.SerializeToElement(new {value = "old-snapshot"}));
		Assert.Equal("draft", page.Field.Text);
		Assert.True(await page.FlushPendingSavesAsync());
		Assert.False(page.Field.IsDirty);
		Assert.Empty(page.Field.ErrorText);
		Assert.Equal(2, attempts);
	});
}
