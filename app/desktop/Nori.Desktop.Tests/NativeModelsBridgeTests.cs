using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Nori.Core.Configuration;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Models;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class NativeModelTestSource(string label = WindowLabels.Models) : IBridgeSource
	{
		public string Label => label;
		public Window? Self => null;
		public void PostEvent(string name, object? payload) { }
	}

	// ---- 初始化握手与窗口状态 ----

	private void InstallKnownModel(string modelId)
	{
		string directory = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, $"{modelId}.model3.json"),
			"{\"FileReferences\":{\"Moc\":\"model.moc3\",\"Textures\":[]}}");
		File.WriteAllText(Path.Combine(directory, "model.moc3"), "MOC3");
	}

	[Fact]
	public async Task model_select与显示参数拒绝未知未安装和越界输入()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_select", Args(new {modelId = "other"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_select", Args(new {modelId = "nori"})));

		InstallKnownModel("nori");
		await commands.InvokeAsync(main, "model_select", Args(new {modelId = "nori"}));
		Assert.Equal("nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_set_display", Args(new {modelId = "nori", opacity = 2})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_set_display", Args(new {modelId = "nori", qualityMode = "unknown"})));
	}
	[Fact]
	public async Task ModelMetadataSeparatesSelectedExpressionsFromAvailableExpressions()
	{
		InstallKnownModel("nori");
		string directory = _services.Resources.ResourceDir(ResourceType.Live2D, "nori");
		File.WriteAllText(Path.Combine(directory, "nori.model3.json"), """
			{
				"FileReferences": {
					"Moc": "model.moc3",
					"Textures": [],
					"Expressions": [{"Name": "Smile", "File": "Smile.exp3.json"}]
				}
			}
			""");
		File.WriteAllText(Path.Combine(directory, "Smile.exp3.json"), "{}");

		BridgeCommands commands = CreateCommands();
		object? initial = await commands.InvokeAsync(
			new NativeModelTestSource(),
			"model_get_meta",
			Args(new {modelId = "nori"}));
		using JsonDocument initialDocument = JsonDocument.Parse(JsonSerializer.Serialize(initial, BridgeJson.Options));
		Assert.Empty(initialDocument.RootElement.GetProperty("selectedExpressions").EnumerateArray());
		Assert.Equal(["Smile"], initialDocument.RootElement.GetProperty("expressions").EnumerateArray().Select(value => value.GetString()));

		_config.Set("l2d_expression_nori", new ConfigValue.Json(JsonNode.Parse("[\"Smile\"]")!));
		object? perModel = await commands.InvokeAsync(
			new NativeModelTestSource(),
			"model_get_meta",
			Args(new {modelId = "nori"}));
		using JsonDocument perModelDocument = JsonDocument.Parse(JsonSerializer.Serialize(perModel, BridgeJson.Options));
		Assert.Equal(["Smile"], perModelDocument.RootElement.GetProperty("selectedExpressions").EnumerateArray().Select(value => value.GetString()));
	}

	[Fact]
	public async Task NativeModelSafeModeKeepsLocalManagementButDisablesAiInteraction()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		BridgeCommands commands = fixture.CreateCommands();
		NativeModelTestSource source = new();
		await commands.InvokeAsync(source, "model_set_behavior", Args(new {aiInteraction = true}));
		Assert.True(fixture._config.GetBoolOr(Nori.Core.Live2D.PetInteractionConfig.AiEnabledKey, false));

		object snapshot = fixture._runtime.BuildSnapshot();
		using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, BridgeJson.Options));
		Assert.True(document.RootElement.GetProperty("app").GetProperty("safeMode").GetBoolean());
		Assert.False(document.RootElement.GetProperty("behaviors").GetProperty("aiInteraction").GetBoolean());
	}

	[Fact]
	public async Task NativeModelCommittedWriteIsNotMaskedByLateCancellation()
	{
		using CancellationTokenSource cancellation = new();
		void CancelAfterCommit() => cancellation.Cancel();
		_runtime.StateChanged += CancelAfterCommit;
		try
		{
			await CreateCommands().InvokeAsync(
				new NativeModelTestSource(),
				"model_set_behavior",
				Args(new {clickInteraction = false}),
				cancellation.Token);
			Assert.True(cancellation.IsCancellationRequested);
			Assert.False(_config.GetBoolOr("l2d_click_interaction", true));
		}
		finally { _runtime.StateChanged -= CancelAfterCommit; }
	}

	[Fact]
	public Task NativeModelServiceUsesExistingCommandsAndStopsNotificationsAfterDispose() => WithSettingsUiAsync(async () =>
	{
		using ModelService service = new(_services, new Window());
		int changes = 0;
		service.StateChanged += () => Interlocked.Increment(ref changes);
		await service.ExecuteAsync("model_set_behavior", new {clickInteraction = false});
		Assert.False(_config.GetBoolOr("l2d_click_interaction", true));
		Assert.True(changes > 0);
		JsonElement snapshot = await service.GetSnapshotAsync();
		Assert.True(snapshot.TryGetProperty("models", out _));
		await service.WaitForPendingOperationsAsync();
		service.Dispose();
		int before = changes;
		_runtime.InvalidateSnapshot();
		Assert.Equal(before, changes);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExecuteAsync("model_get_meta"));
	});

	[Fact]
	public Task NativeModelServiceKeepsCommittedWritesPendingUntilHostReturns() => WithSettingsUiAsync(async () =>
	{
		using ModelService service = new(_services, new Window());
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim release = new();
		void HoldCommittedWrite()
		{
			entered.TrySetResult();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试写入等待超时");
		}
		_runtime.StateChanged += HoldCommittedWrite;
		Task<JsonElement> write = service.ExecuteAsync("model_set_behavior", new {clickInteraction = false});
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
			Assert.False(_config.GetBoolOr("l2d_click_interaction", true));
		}
		finally
		{
			release.Set();
			_runtime.StateChanged -= HoldCommittedWrite;
			await write;
		}
	});
}
