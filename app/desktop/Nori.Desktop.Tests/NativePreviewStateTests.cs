using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Live2DCSharpSDK.App;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Live2D;
using Nori.Desktop.Live2D.Behaviors;
using Nori.Desktop.Models;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativePreviewStateNoGlTimesOutAndCancelsUnderlyingGeneration() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromMilliseconds(100));
		PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
		int ready = 0, failed = 0;
		preview.ModelReady += (_, _) => ready++;
		preview.ModelLoadFailed += (_, args) => { failed++; Assert.Equal(preview.ErrorMessage, args.Message); };
		Task load = preview.LoadModelAsync("nori");
		ModelLoadOperation operation = PreviewStateField<ModelLoadOperation>(runtime, "_pendingModelLoad");
		long generation = runtime.ModelGeneration;

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => load.WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.IsType<TimeoutException>(error.InnerException);
		Assert.Contains("超时", preview.ErrorMessage);
		Assert.Contains("OpenGL", preview.ErrorMessage);
		Assert.False(preview.IsReady);
		Assert.Null(preview.LoadedModelId);
		Assert.Equal(0, ready);
		Assert.Equal(1, failed);
		Assert.True(runtime.ModelGeneration > generation);
		Assert.Null(PreviewStateField<ModelLoadOperation?>(runtime, "_pendingModelLoad"));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
		await operation.PreparationTask;
		Assert.False(PreviewStateField<PetGlControl>(preview, "_glControl").IsVisible);
		Assert.False(runtime.RenderMetrics.Visible);
		Assert.Null(fixture._services.PetRuntime);
		Assert.Equal("arg-nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
	});

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public Task NativePreviewStateOwnerCancellationIsNotAnUnexpectedRuntimeCancellation(bool ownerCancels) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource owner = new();
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromSeconds(2), _ => completion.Task);
		PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
		int failed = 0, ready = 0;
		preview.ModelLoadFailed += (_, _) => failed++;
		preview.ModelReady += (_, _) => ready++;
		Task load = preview.LoadModelAsync("arg-nori", owner.Token);
		long generation = runtime.ModelGeneration;
		if (ownerCancels) owner.Cancel(); else completion.SetCanceled();

		if (ownerCancels)
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.Null(preview.ErrorMessage);
			Assert.Equal(0, failed);
			completion.SetResult();
		}
		else
		{
			InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => load.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
			Assert.Contains("初始化中断", preview.ErrorMessage);
			Assert.Equal(1, failed);
		}
		Assert.True(runtime.ModelGeneration > generation);
		Assert.False(preview.IsReady);
		Assert.Equal(0, ready);
	});

	[Theory]
	[InlineData("arg-nori", false)]
	[InlineData("nori", false)]
	[InlineData("arg-nori", true)]
	[InlineData("nori", true)]
	public Task NativePreviewStateStaleCompletionCannotReplaceNewState(string nextId, bool failLate) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		TaskCompletionSource old = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int loads = 0, failed = 0;
		List<string> ready = [];
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromSeconds(2), _ => ++loads == 1 ? old.Task : current.Task);
		PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
		preview.ModelReady += (_, args) => ready.Add(args.ModelId);
		preview.ModelLoadFailed += (_, _) => failed++;
		Task first = preview.LoadModelAsync("arg-nori");
		Task second = preview.LoadModelAsync(nextId);
		long generation = runtime.ModelGeneration;
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
		current.SetResult();
		await second.WaitAsync(TimeSpan.FromSeconds(3));
		if (failLate) old.SetException(new InvalidOperationException("旧请求失败")); else old.SetResult();
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

		Assert.True(preview.IsReady);
		Assert.Equal(nextId, preview.LoadedModelId);
		Assert.Null(preview.ErrorMessage);
		Assert.Equal(new[] { nextId }, ready);
		Assert.Equal(0, failed);
		Assert.Equal(generation, runtime.ModelGeneration);
		if (failLate) await Assert.ThrowsAsync<InvalidOperationException>(() => old.Task);
	});

	[Fact]
	public Task NativePreviewStateTimeoutCanRetryWithoutLateSuccessOverwritingIt() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		TaskCompletionSource old = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int loads = 0, ready = 0, failed = 0;
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromMilliseconds(100), _ => ++loads == 1 ? old.Task : Task.CompletedTask);
		preview.ModelReady += (_, _) => ready++;
		preview.ModelLoadFailed += (_, _) => failed++;
		await Assert.ThrowsAsync<InvalidOperationException>(() => preview.LoadModelAsync("arg-nori").WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.False(preview.IsReady);
		await preview.LoadModelAsync("nori");
		old.SetResult();
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
		Assert.True(preview.IsReady);
		Assert.Equal("nori", preview.LoadedModelId);
		Assert.Null(preview.ErrorMessage);
		Assert.Equal(1, ready);
		Assert.Equal(1, failed);
	});

	[Theory]
	[InlineData("clear")]
	[InlineData("pause")]
	[InlineData("hide")]
	[InlineData("detach")]
	[InlineData("dispose")]
	public Task NativePreviewStateLifecycleCancellationCannotPublishLateReady(string action) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		TaskCompletionSource old = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int loads = 0, ready = 0, failed = 0;
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromSeconds(2), _ => ++loads == 1 ? old.Task : Task.CompletedTask);
		Window host = new() { Content = preview, Width = 320, Height = 240 };
		try
		{
			host.Show();
			preview.ModelReady += (_, _) => ready++;
			preview.ModelLoadFailed += (_, _) => failed++;
			Task load = preview.LoadModelAsync("arg-nori");
			switch (action)
			{
				case "clear": preview.ClearModel(); break;
				case "pause": preview.Pause(); break;
				case "hide": host.Hide(); break;
				case "detach": host.Content = null; break;
				case "dispose": preview.Dispose(); break;
			}
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.False(preview.IsReady);
			Assert.False(PreviewStateField<PetRuntime>(preview, "_runtime").RenderMetrics.Visible);
			Assert.Null(preview.ErrorMessage);
			Assert.Equal(0, ready);
			Assert.Equal(0, failed);
			if (action != "dispose")
			{
				host.Content = preview;
				host.Show();
				preview.Resume();
				await preview.LoadModelAsync("nori");
			}
			else await Assert.ThrowsAsync<ObjectDisposedException>(() => preview.LoadModelAsync("nori"));
			old.SetResult();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.Equal(action == "dispose" ? 0 : 1, ready);
			Assert.Equal(0, failed);
			Assert.Equal(action == "dispose" ? null : "nori", preview.LoadedModelId);
		}
		finally { host.Close(); }
	});

	[Fact]
	public Task NativePreviewStateReadyForcesAllCachedRenderSettings() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var preview = new ModelPreviewControl(fixture._services, TimeSpan.FromSeconds(2), _ => completion.Task)
		{
			PreviewScale = 1.75f,
			QualityMode = Live2DQualityMode.Quality,
			RenderScale = 2.5f,
			ShadowEnabled = false,
			MaxFps = 30,
		};
		PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
		for (int attempt = 0; attempt < 2; attempt++)
		{
			completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Task load = preview.LoadModelAsync("arg-nori");
			// 模拟提交阶段重置运行时；控件属性未变化也必须重新应用。
			runtime.SetPreviewRenderSettings(0.5f, Live2DQualityMode.Eco, 0.5f, true, 60);
			completion.SetResult();
			await load.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(1.75f, runtime.UserScale);
			Assert.Equal("quality", runtime.QualityMode);
			Assert.Equal(2.5f, runtime.RenderScale);
			Assert.False(runtime.ShadowEnabled);
			Assert.Equal(30, runtime.MaxFps);
		}
	});

	[Fact]
	public Task NativePreviewStateWindowInheritsOnlyLocalVisualBehaviors() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeModels(fixture);
		foreach (string key in new[] { "auto_blink", "eye_tracking", "idle_eye_animation", "idle_animation", "expression_enabled", "lip_sync", "click_interaction" })
			fixture._config.Set("l2d_" + key, new ConfigValue.Boolean(false));
		fixture._config.Set("l2d_beat_sync", new ConfigValue.Boolean(true));
		fixture._config.Set("l2d_click_through", new ConfigValue.Boolean(true));
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori");
			ModelPreviewControl preview = PreviewStateField<ModelPreviewControl>(window, "_preview");
			PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
			await FinishPreviewStateLoadAsync(preview);
			Assert.False(runtime.AutoBlinkEnabled);
			Assert.False(runtime.EyeTrackingEnabled);
			Assert.False(runtime.IdleEyeAnimationEnabled);
			Assert.False(runtime.IdleAnimationEnabled);
			Assert.False(runtime.ExpressionEnabled);
			Assert.False(runtime.LipSyncEnabled);
			Assert.False(runtime.ClickInteraction);
			Assert.True(runtime.BeatSyncEnabled);
			Assert.False(runtime.ClickThroughEnabled);
			Assert.True(runtime.IsPreviewMode);
			Assert.Null(fixture._services.PetRuntime);
			Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativePreviewStateWindowOwnerCancellationClearsLoadingFooter() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori");
			ModelPreviewControl preview = PreviewStateField<ModelPreviewControl>(window, "_preview");
			PreviewStateField<CancellationTokenSource>(window, "_previewLoadCancellation").Cancel();
			await WaitUntilAsync(() => PreviewStateField<CancellationTokenSource?>(window, "_previewLoadCancellation") is null);
			Assert.False(preview.IsReady);
			Assert.Null(preview.ErrorMessage);
			Assert.False(PreviewStateField<TextBlock>(window, "_previewStatus").IsVisible);
			Assert.Equal("已自动保存", PreviewStateField<TextBlock>(window, "_status").Text);
			await window.OpenAdjustAsync("arg-nori");
			await FinishPreviewStateLoadAsync(preview);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativePreviewStateFullReadyResetsExpressionCacheAndClearRemovesLoadingFooter() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeModels(fixture);
		fixture._config.Set("l2d_expression_arg-nori", new ConfigValue.Json(JsonNode.Parse("[\"Angry\"]")!));
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori");
			ModelPreviewControl preview = PreviewStateField<ModelPreviewControl>(window, "_preview");
			PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
			ExpressionEntry expression = RegisterPreviewStateExpression(runtime);
			await FinishPreviewStateLoadAsync(preview);
			Assert.Equal(1, expression.CurrentValue);
			TextBlock footer = PreviewStateField<TextBlock>(window, "_status");
			Assert.Equal("已自动保存", footer.Text);
			Assert.False(PreviewStateField<TextBlock>(window, "_previewStatus").IsVisible);

			Task reload = preview.LoadModelAsync("arg-nori");
			expression = RegisterPreviewStateExpression(runtime);
			await FinishPreviewStateLoadAsync(preview);
			await reload;
			Assert.Equal(1, expression.CurrentValue);
			Task pending = preview.LoadModelAsync("arg-nori");
			footer.Text = "正在读取模型…";
			Assert.True(await window.CloseAdjustAsync());
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
			Assert.Equal("已自动保存", footer.Text);
			Assert.False(preview.IsReady);
			Assert.Null(preview.LoadedModelId);
			Assert.Equal(0, preview.MinHeight);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativePreviewStateNoneStopsLocalTestsAndBackgroundTapRespectsToggleAlphaAndThrottle() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		PetRuntime? runtime = null;
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			ModelPreviewControl preview = PreviewStateField<ModelPreviewControl>(window, "_preview");
			runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
			ExpressionEntry expression = RegisterPreviewStateExpression(runtime);
			await FinishPreviewStateLoadAsync(preview);
			PetGlControl gl = PreviewStateField<PetGlControl>(preview, "_glControl");
			byte[] mask = PreviewStateField<byte[]>(gl, "_maskBits");
			Point point = new(preview.Bounds.Width / 2, preview.Bounds.Height / 2);
			var region = new PetInteractionRegion
			{
				Id = "test", Name = "本地测试", ReactionMode = PetInteractionReactionMode.Ai,
				Rect = new PetInteractionRect { X = 0, Y = 0, Width = 1, Height = 1 },
				Expression = new PetInteractionAction { Mode = PetInteractionActionMode.Selected, Name = "Angry" },
			};
			// 仅走命中区域的纯表情分支，不调用合成模型的 SDK 或 GL 方法。
			PreviewStateSet(runtime, "_currentModel", RuntimeHelpers.GetUninitializedObject(typeof(LAppModel)));
			PreviewStateSet(runtime, "_viewportMapping", PetViewportMapping.FromFinalTransform(preview.Bounds.Width, preview.Bounds.Height, 2, 2, 1, 1));
			runtime.SetInteractionConfig("arg-nori", new PetInteractionConfig { Regions = [region] });
			int aiRequests = 0;
			runtime.InteractionTriggered += _ => aiRequests++;
			ToggleButton none = ModelControl<ToggleButton>(window, "ModelsExpression_");
			ModelDrafts(window)["behavior:clickInteraction"].AcceptSnapshot(false);
			UpdatePreviewStateWindow(window);
			Array.Fill(mask, byte.MaxValue);
			foreach (bool visible in new[] { false, true })
			{
				window.RegionOverlay.IsVisible = visible;
				Assert.False(preview.TapAt(point));
				Assert.Equal(0, expression.CurrentValue);
			}
			Array.Clear(mask);
			PreviewStateSet(runtime, "_lastTapTime", (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
			for (int attempt = 0; attempt < 2; attempt++)
			{
				Assert.True(preview.TestRegion(region));
				UpdatePreviewStateWindow(window);
				Assert.Equal(1, expression.CurrentValue);
				none.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
				Assert.Equal(0, expression.CurrentValue);
			}

			ModelDrafts(window)["behavior:clickInteraction"].AcceptSnapshot(true);
			UpdatePreviewStateWindow(window);
			Assert.False(preview.TapAt(point));
			Array.Fill(mask, byte.MaxValue);
			foreach (bool visible in new[] { false, true })
			{
				window.RegionOverlay.IsVisible = visible;
				window.RegionOverlay.Editing = false;
				PreviewStateSet(runtime, "_lastTapTime", 0d);
				if (visible) window.RegionOverlay.BeginGesture(point); else Assert.True(preview.TapAt(point));
				UpdatePreviewStateWindow(window);
				Assert.Equal(1, expression.CurrentValue);
				none.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
				Assert.Equal(0, expression.CurrentValue);
				Assert.True(preview.TapAt(point));
				Assert.Equal(0, expression.CurrentValue);
			}
			Assert.Equal(0, aiRequests);
			Assert.Null(fixture._services.PetRuntime);
			Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			await window.PrepareShutdownAsync();
		}
		finally
		{
			if (runtime is not null) PreviewStateSet(runtime, "_currentModel", null);
			window.AllowClose = true; window.Close();
		}
	});

	private static T PreviewStateField<T>(object target, string name) =>
		(T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

	private static void PreviewStateSet(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private static void UpdatePreviewStateWindow(ModelsWindow window) =>
		typeof(ModelsWindow).GetMethod("UpdatePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

	private static async Task FinishPreviewStateLoadAsync(ModelPreviewControl preview)
	{
		// 只模拟 GL 提交完成信号；资源全部来自临时目录，不声称验证真实渲染。
		PetRuntime runtime = PreviewStateField<PetRuntime>(preview, "_runtime");
		PreviewStateField<ModelLoadOperation>(runtime, "_pendingModelLoad").Complete();
		await WaitUntilAsync(() => preview.IsReady);
	}

	private static ExpressionEntry RegisterPreviewStateExpression(PetRuntime runtime)
	{
		var entry = new ExpressionEntry { Name = "Angry", ParameterId = "ParamTest", Blend = ExpressionBlendMode.Add, TargetValue = 1 };
		PreviewStateField<ExpressionStore>(runtime, "_expressionStore").RegisterExpressions("arg-nori", [], [entry]);
		return entry;
	}
}
