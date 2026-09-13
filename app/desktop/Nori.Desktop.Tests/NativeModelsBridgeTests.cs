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
	private sealed class NativeModelTestSource(bool visible = true, string label = WindowLabels.Models) : INativeModelSource
	{
		public string Label => label;
		public bool IsVisible => visible;
		public Window? Self => null;
		public void PostEvent(string name, object? payload) { }
		public void PostResult(long id, object? value, string? error) { }
	}

	[Fact]
	public async Task WindowOpenModelsAcceptsOnlyVisibleMainSource()
	{
		BridgeCommands commands = CreateCommands();
		Assert.Null(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"window_open_models",
			Args(new { })));
		Assert.Equal(1, _windows.ModelsShowCount);

		IBridgeSource[] rejected =
		[
			new FakeBridgeSource(WindowLabels.Main, false),
			new FakeBridgeSource(WindowLabels.Init),
			new NativeModelTestSource(),
			new NativeModelTestSource(label: WindowLabels.Main),
		];
		foreach (IBridgeSource source in rejected)
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				commands.InvokeAsync(source, "window_open_models", Args(new { })));
		Assert.Equal(1, _windows.ModelsShowCount);
	}

	[Fact]
	public async Task NativeModelPolicyCannotBeBypassedAtEitherHostEntry()
	{
		string[] allowed =
		[
			"model_get_meta", "model_import_local", "model_list", "model_select",
			"model_set_behavior", "model_set_display", "model_set_interactions",
		];
		Assert.Equal(allowed, ModelService.Commands.Order(StringComparer.Ordinal));

		NativeModelTestSource source = new();
		foreach (string command in new[] {"plugin_widgets", "memory_overview", "ui_get_snapshot", "model_future_command"})
		{
			Assert.False(ModelService.IsCommandAllowed(command));
			InvalidOperationException direct = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				CreateCommands().InvokeAsync(source, command, Args(new { })));
			Assert.Contains("原生模型窗口不允许", direct.Message);
			InvalidOperationException routed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				new BridgeCommandRouter(_services).InvokeAsync(source, command, Args(new { })));
			Assert.Contains("原生模型窗口不允许", routed.Message);
		}
	}

	[Fact]
	public async Task NativeModelCommandsRequireTrustedMarker()
	{
		InstallKnownModel("nori");
		BridgeCommands commands = CreateCommands();
		Assert.NotNull(await commands.InvokeAsync(new NativeModelTestSource(), "model_list", Args(new { })));
		Assert.NotNull(await commands.InvokeAsync(new NativeModelTestSource(), "model_get_meta", Args(new {modelId = "nori"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Models), "model_list", Args(new { })));
	}

	[Fact]
	public async Task NativeModelImportRequiresVisibleOwner()
	{
		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(
				new NativeModelTestSource(false),
				"model_import_local",
				Args(new {resourceType = "live2d", sourceKind = "zip", filePath = "missing.zip"})));
		Assert.Contains("不可见", exception.Message);
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
		_config.Set("l2d_expression", new ConfigValue.Json(JsonNode.Parse("[\"LegacyPose\"]")!));

		BridgeCommands commands = CreateCommands();
		object? fallback = await commands.InvokeAsync(
			new NativeModelTestSource(),
			"model_get_meta",
			Args(new {modelId = "nori"}));
		using JsonDocument fallbackDocument = JsonDocument.Parse(JsonSerializer.Serialize(fallback, BridgeJson.Options));
		Assert.Equal(["LegacyPose"], fallbackDocument.RootElement.GetProperty("selectedExpressions").EnumerateArray().Select(value => value.GetString()));
		Assert.Equal(["Smile"], fallbackDocument.RootElement.GetProperty("expressions").EnumerateArray().Select(value => value.GetString()));

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
		Assert.NotNull(await commands.InvokeAsync(source, "model_list", Args(new { })));
		await commands.InvokeAsync(source, "model_set_behavior", Args(new {aiInteraction = true}));
		Assert.True(fixture._config.GetBoolOr(Nori.Core.Live2D.PetInteractionConfig.AiEnabledKey, false));

		object? snapshot = await commands.InvokeAsync(source, "model_list", Args(new { }));
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
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync("chat_clear"));
		await service.WaitForPendingOperationsAsync();
		service.Dispose();
		int before = changes;
		_runtime.InvalidateSnapshot("models");
		Assert.Equal(before, changes);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExecuteAsync("model_list"));
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
