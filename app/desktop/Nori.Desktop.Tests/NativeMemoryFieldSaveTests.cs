using Nori.Desktop.Memory;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeMemoryFieldSerializesNewDraftWhilePreviousSaveIsRunning() => WithSettingsUiAsync(async () =>
	{
		List<object> saved = [];
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MemorySettingDraft draft = new(0, async value =>
		{
			saved.Add(value);
			if (saved.Count == 1) await release.Task;
		}, () => { });
		draft.Set(1);
		Task<bool> flush = draft.FlushAsync();
		Assert.True(draft.Saving);
		draft.Set(2);
		draft.AcceptSnapshot(99);
		Assert.Equal(2, draft.Value);
		release.SetResult();
		Assert.True(await flush);
		Assert.Equal(new object[] {1, 2}, saved);
		Assert.False(draft.Dirty);
		Assert.False(draft.Saving);
		draft.AcceptSnapshot(3);
		Assert.Equal(3, draft.Value);
		await draft.FlushAsync();
	});

	[Fact]
	public Task NativeMemoryFieldKeepsFailedValueUntilExplicitRetrySucceeds() => WithSettingsUiAsync(async () =>
	{
		bool fail = true;
		List<object> saved = [];
		MemorySettingDraft draft = new(6, value =>
		{
			if (fail) throw new InvalidOperationException("合成写入失败");
			saved.Add(value);
			return Task.CompletedTask;
		}, () => { });
		draft.Set(9);
		Assert.False(await draft.FlushAsync());
		Assert.True(draft.Dirty);
		Assert.False(draft.Saving);
		Assert.Contains("合成写入失败", draft.Error);
		draft.AcceptSnapshot(6);
		Assert.Equal(9, draft.Value);
		fail = false;
		Assert.True(await draft.FlushAsync());
		Assert.Equal(new object[] {9}, saved);
		Assert.Empty(draft.Error);
		Assert.False(draft.Dirty);
	});

	[Fact]
	public Task NativeMemoryFieldsDoNotShareSaveGates() => WithSettingsUiAsync(async () =>
	{
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MemorySettingDraft slow = new(1, _ => release.Task, () => { });
		object? savedFast = null;
		MemorySettingDraft fast = new(2, value => { savedFast = value; return Task.CompletedTask; }, () => { });
		slow.Set(3);
		Task<bool> slowFlush = slow.FlushAsync();
		fast.Set(4);
		Assert.True(await fast.FlushAsync());
		Assert.Equal(4, savedFast);
		Assert.False(slowFlush.IsCompleted);
		release.SetResult();
		Assert.True(await slowFlush);
	});
}
