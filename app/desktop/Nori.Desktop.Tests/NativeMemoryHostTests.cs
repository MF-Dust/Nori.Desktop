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
	public async Task memory_transfer只允许可见main窗口()
	{
		BridgeCommands commands = CreateCommands();
		string[] commandsToCheck = ["memory_export", "memory_import_preview", "memory_import_commit"];
		foreach (string command in commandsToCheck)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Init), command, Args(new {fileContent = "{}"})));
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main, false), command, Args(new {fileContent = "{}"})));
		}
	}

	[Fact]
	public async Task memory_transfer桥接使用服务端预览并返回既有前端DTO()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		string content = $"bridge-transfer-{Guid.NewGuid():N}";
		string transfer = JsonSerializer.Serialize(new
		{
			version = "nori-memory-v1",
			format = "nori-memory-v1",
			memories = new[]
			{
				new
				{
					content,
					canonical_summary = content,
					kind = "preference",
					importance = 0.9,
					confidence = 0.8,
					tags = "coffee",
				},
			},
		});
		int snapshotBefore = _runtime.SnapshotVersion;
		Assert.Empty(_services.Memory.GetAll());

		object? previewObject = await commands.InvokeAsync(main, "memory_import_preview", Args(new
		{
			fileContent = transfer,
			fileName = "memory.json",
			fileSize = 1,
		}));
		using JsonDocument preview = JsonDocument.Parse(JsonSerializer.Serialize(previewObject, BridgeJson.Options));
		Assert.True(preview.RootElement.GetProperty("valid").GetBoolean());
		Assert.Equal(1, preview.RootElement.GetProperty("newCount").GetInt32());
		Assert.Equal("none", preview.RootElement.GetProperty("items")[0].GetProperty("conflictType").GetString());
		string token = preview.RootElement.GetProperty("previewToken").GetString()!;

		object? commitObject = await commands.InvokeAsync(main, "memory_import_commit", Args(new
		{
			previewToken = token,
			conflictStrategy = "skip",
			items = new[] {new {content = "客户端伪造内容", kind = "identity"}},
		}));
		using JsonDocument commit = JsonDocument.Parse(JsonSerializer.Serialize(commitObject, BridgeJson.Options));
		Assert.True(commit.RootElement.GetProperty("success").GetBoolean());
		Assert.Equal(1, commit.RootElement.GetProperty("importedCount").GetInt32());
		Assert.Equal(0, commit.RootElement.GetProperty("updatedCount").GetInt32());
		Assert.Equal(0, commit.RootElement.GetProperty("skippedCount").GetInt32());
		Assert.True(_runtime.SnapshotVersion > snapshotBefore);
		Nori.Core.Memory.MemoryItem imported = Assert.Single(_services.Memory.GetAll());
		Assert.Equal(content, imported.Content);
		Assert.Equal("memory_transfer", imported.Source);
		Assert.Single(_services.Memory.GetAtoms(imported.Id));

		object? exportObject = await commands.InvokeAsync(main, "memory_export", Args(new { }));
		using JsonDocument export = JsonDocument.Parse(JsonSerializer.Serialize(exportObject, BridgeJson.Options));
		Assert.Equal(1, export.RootElement.GetProperty("totalCount").GetInt32());
		string exportContent = export.RootElement.GetProperty("content").GetString()!;
		using JsonDocument exportDocument = JsonDocument.Parse(exportContent);
		JsonElement exported = Assert.Single(exportDocument.RootElement.GetProperty("memories").EnumerateArray());
		Assert.False(exported.TryGetProperty("embedding", out _));
		Assert.False(exported.TryGetProperty("status", out _));

		const string secret = "bridge-secret-must-not-leak";
		object? invalidObject = await commands.InvokeAsync(main, "memory_import_preview", Args(new
		{
			fileContent = $"{{\"version\":\"nori-memory-v1\",\"memories\":[{{\"content\":\"安全\",\"kind\":\"general\",\"embedding\":\"{secret}\"}}]}}",
		}));
		string invalidJson = JsonSerializer.Serialize(invalidObject, BridgeJson.Options);
		Assert.DoesNotContain(secret, invalidJson, StringComparison.Ordinal);
		Assert.Contains("记忆传输条目不符合安全格式", invalidJson, StringComparison.Ordinal);
	}
	[Fact]
	public async Task NativeMemoryReadsRequireTrustedMarkerAndPreserveWebSourceRules()
	{
		BridgeCommands commands = CreateCommands();
		Assert.NotNull(await commands.InvokeAsync(new NativeMemoryTestSource(), "memory_list_page", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Memory), "memory_list_page", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Pet), "memory_list_page", Args(new { })));
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
		foreach (string command in new[] {"memory_reembed_all", "memory_recall_debug", "memory_knowledge_reindex"})
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
		JsonElement page = await service.ExecuteAsync("memory_list_page");
		Assert.True(page.TryGetProperty("items", out _));
		Assert.True(page.TryGetProperty("total", out _));
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
		_runtime.InvalidateSnapshot();
		Assert.Equal(before, changes);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExecuteAsync("memory_list_page"));
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
