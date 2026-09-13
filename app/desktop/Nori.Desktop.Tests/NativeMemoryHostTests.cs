using System.Text.Json;
using Avalonia.Controls;
using Nori.Core.Memory;
using Nori.Desktop.Bridge;
using MemoryService = Nori.Desktop.Memory.MemoryService;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class NativeMemoryTestSource(bool visible = true, string label = WindowLabels.Memory) : INativeMemorySource
	{
		public string Label => label;
		public bool IsVisible => visible;
		public Window? Self => null;
		public void PostEvent(string name, object? payload) { }
		public void PostResult(long id, object? value, string? error) { }
	}

	[Theory]
	[InlineData("overview")]
	[InlineData("memories")]
	[InlineData("atoms")]
	[InlineData("knowledge")]
	[InlineData("archive")]
	[InlineData("transfer")]
	[InlineData("debugger")]
	[InlineData("advanced")]
	[InlineData(null)]
	public async Task WindowOpenMemoryAcceptsVisibleMainAndKnownPages(string? page)
	{
		await CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_open_memory", Args(new {page}));
		Assert.Equal([page], _windows.MemoryPages);
	}

	[Fact]
	public async Task WindowOpenMemoryRejectsHiddenForeignAndRecursiveSources()
	{
		BridgeCommands commands = CreateCommands();
		IBridgeSource[] rejected = [new FakeBridgeSource(WindowLabels.Main, false), new FakeBridgeSource(WindowLabels.Init), new NativeMemoryTestSource(), new NativeMemoryTestSource(label: WindowLabels.Main)];
		foreach (IBridgeSource source in rejected)
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "window_open_memory", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_open_memory", Args(new {page = "unknown"})));
		Assert.Empty(_windows.MemoryPages);
	}

	[Theory]
	[InlineData("chat_clear")]
	[InlineData("settings_update_general")]
	[InlineData("plugin_list")]
	[InlineData("automation_get_snapshot")]
	[InlineData("memory_future_command")]
	[InlineData("ui_get_snapshot")]
	[InlineData("write_log")]
	public async Task NativeMemoryPolicyCannotBeBypassedAtEitherHostEntry(string command)
	{
		NativeMemoryTestSource source = new();
		Assert.False(MemoryService.IsCommandAllowed(command));
		InvalidOperationException direct = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(source, command, Args(new { })));
		Assert.Contains("原生记忆窗口不允许", direct.Message);
		InvalidOperationException routed = await Assert.ThrowsAsync<InvalidOperationException>(() => new BridgeCommandRouter(_services).InvokeAsync(source, command, Args(new { })));
		Assert.Contains("原生记忆窗口不允许", routed.Message);
	}

	[Fact]
	public async Task NativeMemoryReadsRequireTrustedMarkerAndPreserveWebSourceRules()
	{
		BridgeCommands commands = CreateCommands();
		Assert.NotNull(await commands.InvokeAsync(new NativeMemoryTestSource(), "memory_overview", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Memory), "memory_overview", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Pet), "memory_list", Args(new { })));
	}

	[Theory]
	[InlineData("memory_export")]
	[InlineData("memory_import_preview")]
	[InlineData("memory_import_commit")]
	public async Task NativeMemoryTransferStillRequiresVisibleOwner(string command)
	{
		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(new NativeMemoryTestSource(false), command, Args(new { })));
		Assert.Contains("不可见", exception.Message);
	}

	[Fact]
	public async Task NativeMemoryDestructiveActionsStillRequireExplicitConfirmation()
	{
		BridgeCommands commands = CreateCommands();
		NativeMemoryTestSource source = new();
		MemoryItem item = Assert.IsType<MemoryItem>(await commands.InvokeAsync(source, "memory_add", Args(new {content = "用于验证原生记忆权限的合成内容"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "memory_delete", Args(new {id = item.Id})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "memory_clear", Args(new {confirmToken = "wrong"})));
		Assert.NotNull(_services.Memory.Get(item.Id));
		Assert.Equal(true, await commands.InvokeAsync(source, "memory_delete", Args(new {id = item.Id, confirmToken = "DELETE_MEMORY"})));
		Assert.Null(_services.Memory.Get(item.Id));
	}

	[Fact]
	public async Task NativeMemorySafeModeAllowsLocalReadsButBlocksNetworkOperations()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		BridgeCommands commands = fixture.CreateCommands();
		NativeMemoryTestSource source = new();
		Assert.NotNull(await commands.InvokeAsync(source, "memory_list_page", Args(new { })));
		foreach (string command in new[] {"memory_search_hybrid", "memory_reembed_all", "memory_recall_debug", "memory_knowledge_reindex"})
		{
			InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, command, Args(new { })));
			Assert.Contains("安全模式", exception.Message);
		}
	}

	[Fact]
	public async Task NativeMemoryCommittedWriteIsNotMaskedByLateCancellation()
	{
		using CancellationTokenSource cancellation = new();
		void CancelAfterCommit() => cancellation.Cancel();
		_runtime.StateChanged += CancelAfterCommit;
		try
		{
			await CreateCommands().InvokeAsync(new NativeMemoryTestSource(), "memory_update_settings", Args(new {settings = new {recallTopK = 8}}), cancellation.Token);
			Assert.True(cancellation.IsCancellationRequested);
			Assert.Equal(8, _runtime.Memory.Settings.RecallTopK);
		}
		finally { _runtime.StateChanged -= CancelAfterCommit; }
	}

	[Fact]
	public async Task NativeMemoryKnowledgeReindexReceivesCancellationBeforeFileRead()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		using CancellationTokenSource cancellation = new();
		void CancelWhenIndexStarts() => cancellation.Cancel();
		fixture._runtime.StateChanged += CancelWhenIndexStarts;
		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateCommands().InvokeAsync(
				new NativeMemoryTestSource(), "memory_knowledge_reindex", Args(new { }), cancellation.Token));
		}
		finally { fixture._runtime.StateChanged -= CancelWhenIndexStarts; }
	}

	[Fact]
	public Task NativeMemoryCancelsMaintenanceAndAllowsFreshQueries() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		using MemoryService service = new(fixture._services, new Window());
		void CancelWhenIndexStarts() => service.CancelBackgroundOperations();
		fixture._runtime.StateChanged += CancelWhenIndexStarts;
		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync("memory_knowledge_reindex"));
		}
		finally { fixture._runtime.StateChanged -= CancelWhenIndexStarts; }
		await service.WaitForPendingOperationsAsync();
		JsonElement overview = await service.ExecuteAsync("memory_overview");
		Assert.True(overview.TryGetProperty("activeMemories", out _));
	});

	[Fact]
	public Task NativeMemoryServiceUsesExistingStoreAndStopsNotificationsAfterDispose() => WithSettingsUiAsync(async () =>
	{
		using MemoryService service = new(_services, new Window());
		int changes = 0;
		service.StateChanged += () => Interlocked.Increment(ref changes);
		await service.ExecuteAsync("memory_update_settings", new {settings = new {recallTopK = 7}});
		Assert.Equal(7, _runtime.Memory.Settings.RecallTopK);
		Assert.True(changes > 0);
		JsonElement snapshot = await service.GetSnapshotAsync();
		Assert.True(snapshot.TryGetProperty("general", out _));
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync("chat_clear"));
		await service.WaitForPendingOperationsAsync();
		service.Dispose();
		int before = changes;
		_runtime.InvalidateSnapshot("memory");
		Assert.Equal(before, changes);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExecuteAsync("memory_overview"));
	});

	[Fact]
	public Task NativeMemoryServiceTracksCommittedWriteUntilHostActuallyReturns() => WithSettingsUiAsync(async () =>
	{
		using MemoryService service = new(_services, new Window());
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim release = new();
		void HoldCommittedWrite()
		{
			entered.TrySetResult();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试写入等待超时");
		}
		_runtime.StateChanged += HoldCommittedWrite;
		Task<JsonElement> write = service.ExecuteAsync("memory_update_settings", new {settings = new {recallTopK = 9}});
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			service.CancelBackgroundOperations();
			service.Dispose();
			Task drained = service.WaitForPendingOperationsAsync();
			Assert.False(write.IsCompleted);
			Assert.False(drained.IsCompleted);
			release.Set();
			await write;
			await drained;
			Assert.Equal(9, _runtime.Memory.Settings.RecallTopK);
		}
		finally
		{
			release.Set();
			_runtime.StateChanged -= HoldCommittedWrite;
			await write;
		}
	});
}
