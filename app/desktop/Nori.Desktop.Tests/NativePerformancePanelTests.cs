using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Live2D;
using Nori.Desktop.Live2D.Gl;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public void 没有运行中的模型时性能指标不可用且不显示默认模型ID()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services, previewMode: false);
		Assert.Equal("arg-nori", runtime.CurrentModelId);
		Assert.Null(runtime.CurrentModel);
		DateTime now = DateTime.UtcNow;
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, true, now, null);
		Assert.Equal(Live2DPerformanceAvailability.SafeMode, snapshot.Availability);
		Assert.Null(snapshot.SafeModelId);
		Assert.Null(snapshot.TargetFps);
		Assert.Null(snapshot.RenderScale);
		Assert.Null(snapshot.FrameTimeP95Ms);
		Assert.Null(snapshot.MaskCostP95Ms);
		Assert.Null(snapshot.DroppedFrames);
		Assert.Null(snapshot.ShadowRequested);
		Assert.Null(snapshot.OffscreenRendering);
		Assert.Null(snapshot.SampledAtUtc);
		Dictionary<string, string> diagnostic = snapshot.ToDiagnosticValues();
		Assert.Equal("unavailable", diagnostic["live2d_effective_fps"]);
		Assert.Equal("unavailable", diagnostic["live2d_frame_time_p95_ms"]);
		Assert.Equal("unavailable", diagnostic["live2d_dropped_frames"]);
		Assert.Equal("unavailable", diagnostic["live2d_model_id"]);
		Assert.Equal("safe_mode", diagnostic["live2d_status"]);
		SettingsLocalization.SetLanguage("zh-CN");
		Live2DPerformancePresentation text = Live2DPerformanceText.Present(snapshot);
		Assert.Equal("安全模式", text.Status);
		Assert.DoesNotContain("arg-nori", text.CopyText, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("不可用", text.CopyText, StringComparison.Ordinal);
		Assert.DoesNotContain("\n目标帧率: 0", text.CopyText, StringComparison.Ordinal);
	}

	[Fact]
	public void 没有模型时可见采样不算正在渲染()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		Assert.Null(runtime.CurrentModel);
		runtime.RecordRenderMetrics(16.7, 1.2);
		runtime.SetRenderSurfaceState(true, true, true);
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Unavailable, snapshot.Availability);
		Assert.Null(snapshot.TargetFps);
		Assert.Null(snapshot.FrameTimeP95Ms);
		Assert.Null(snapshot.DroppedFrames);
		Assert.Null(snapshot.ShadowApplied);
		Assert.Equal(0, snapshot.FrameSampleCount);
		SettingsLocalization.SetLanguage("zh-CN");
		Live2DPerformancePresentation text = Live2DPerformanceText.Present(snapshot);
		Assert.Equal("没有可展示的实时采样", text.Status);
		Assert.Contains("当前没有正在渲染的模型", text.CopyText, StringComparison.Ordinal);
	}

	[Fact]
	public void 沿用现有上下文的成功帧结束重建()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		PetGlControl control = new(runtime);
		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Rebuilding);
		control.ObserveSuccessfulFrame();
		Assert.Equal(PetRenderRecoveryPhase.Rebuilding, runtime.RenderRecovery);

		control.ArmExistingContextResume();
		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.ContextLost);
		control.ObserveSuccessfulFrame();
		Assert.Equal(PetRenderRecoveryPhase.ContextLost, runtime.RenderRecovery);

		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Rebuilding);
		control.ArmExistingContextResume();
		control.ObserveSuccessfulFrame();
		Assert.Equal(PetRenderRecoveryPhase.Restored, runtime.RenderRecovery);
		MarkPerformanceModel(runtime, "nori");
		runtime.RecordRenderMetrics(16, 1);
		runtime.SetRenderSurfaceState(true, false, true);
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Live, snapshot.Availability);
		Assert.True(snapshot.SurfaceRestored);
	}

	[Fact]
	public void 有帧样本时展示真实读数且丢帧为零仍然有效()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		MarkPerformanceModel(runtime, "ARG-NORI");
		runtime.RecordRenderMetrics(16.7, 1.25);
		runtime.RecordRenderMetrics(double.NaN, 0);
		runtime.SetRenderSurfaceState(true, true, true);
		DateTime now = DateTime.UtcNow;
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, false, now, 42 * 1024 * 1024);
		Assert.Equal(Live2DPerformanceAvailability.Live, snapshot.Availability);
		Assert.Equal("arg-nori", snapshot.SafeModelId);
		Assert.Equal(60, snapshot.TargetFps);
		Assert.NotNull(snapshot.RenderScale);
		Assert.True(snapshot.RenderScale > 0);
		Assert.Equal(16.7, snapshot.FrameTimeP95Ms);
		Assert.Equal(1.25, snapshot.MaskCostP95Ms);
		Assert.Equal(0, snapshot.DroppedFrames);
		Assert.True(snapshot.ShadowRequested);
		Assert.True(snapshot.ShadowApplied);
		Assert.True(snapshot.OffscreenRendering);
		Assert.NotNull(snapshot.SampledAtUtc);
		Assert.Equal(42 * 1024 * 1024, snapshot.ProcessWorkingSetBytes);
		SettingsLocalization.SetLanguage("en-US");
		try
		{
			Live2DPerformancePresentation text = Live2DPerformanceText.Present(snapshot);
			Assert.Equal("Rendering", text.Status);
			Assert.Contains("Target frame rate: 60 FPS", text.CopyText, StringComparison.Ordinal);
			Assert.Contains("Dropped frames: 0", text.CopyText, StringComparison.Ordinal);
			Assert.Contains("Model: arg-nori", text.CopyText, StringComparison.Ordinal);
			Assert.Contains("Current quality: Quality", text.CopyText, StringComparison.Ordinal);
			Assert.Contains("Process working set: 42.0 MB", text.CopyText, StringComparison.Ordinal);
		}
		finally { SettingsLocalization.SetLanguage("zh-CN"); }
	}

	[Fact]
	public void 过期暂停失败和恢复状态不会显示成成功()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		MarkPerformanceModel(runtime, "nori");
		runtime.RecordRenderMetrics(20, 2);
		runtime.SetRenderSurfaceState(false, false, true);
		SetQualityField(runtime, "_lastFrameSampleUtc", DateTime.UtcNow - TimeSpan.FromSeconds(30));
		SettingsLocalization.SetLanguage("zh-CN");
		Live2DPerformanceSnapshot stale = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Stale, stale.Availability);
		Assert.Equal(20, stale.FrameTimeP95Ms);
		Assert.Contains("采样已过期", Live2DPerformanceText.Present(stale).Status, StringComparison.Ordinal);

		runtime.SetRenderSurfaceState(false, false, false);
		Live2DPerformanceSnapshot paused = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Paused, paused.Availability);
		Assert.Contains("渲染已暂停", Live2DPerformanceText.Present(paused).CopyText, StringComparison.Ordinal);

		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.ContextLost);
		Live2DPerformanceSnapshot lost = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Recovering, lost.Availability);
		Assert.Contains("上下文已丢失", Live2DPerformanceText.Present(lost).CopyText, StringComparison.Ordinal);
		Assert.DoesNotContain("正在渲染", Live2DPerformanceText.Present(lost).Status, StringComparison.Ordinal);

		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Failed);
		Live2DPerformanceSnapshot failed = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.RenderFailed, failed.Availability);
		Assert.Contains("渲染初始化失败", Live2DPerformanceText.Present(failed).Status, StringComparison.Ordinal);

		runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Restored);
		runtime.SetRenderSurfaceState(true, false, true);
		SetQualityField(runtime, "_lastFrameSampleUtc", DateTime.UtcNow);
		Live2DPerformanceSnapshot restored = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.Live, restored.Availability);
		Assert.True(restored.SurfaceRestored);
		Assert.Contains("已从故障中恢复", Live2DPerformanceText.Present(restored).CopyText, StringComparison.Ordinal);
	}

	[Fact]
	public void 模型加载失败保留失败状态并去掉路径和凭据()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		string leaked = LoadFailureProbe("/home/user/hidden", includeToken: true);
		SetQualityField(runtime, "<LastModelLoadError>k__BackingField", leaked);
		runtime.RecordRenderMetrics(12, 1);
		runtime.SetRenderSurfaceState(true, false, true);
		Live2DPerformanceSnapshot hidden = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.LoadFailed, hidden.Availability);
		Assert.Null(hidden.FrameTimeP95Ms);
		Assert.Null(hidden.SafeModelId);
		SettingsLocalization.SetLanguage("zh-CN");
		string hiddenText = Live2DPerformanceText.Present(hidden).CopyText;
		Assert.Contains("模型加载失败", hiddenText, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user", hiddenText, StringComparison.Ordinal);
		Assert.DoesNotContain(CredentialName(), hiddenText, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(TokenPhrase(), hiddenText, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("hidden", hiddenText, StringComparison.OrdinalIgnoreCase);

		MarkPerformanceModel(runtime, "nori");
		SetQualityField(runtime, "<LastModelLoadError>k__BackingField", "模型 arg-nori 加载失败: /tmp/hidden/model " + CredentialPhrase());
		Live2DPerformanceSnapshot visible = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.LoadFailed, visible.Availability);
		Assert.Equal("arg-nori", visible.FailedModelId);
		Assert.Equal("nori", visible.SafeModelId);
		string visibleText = Live2DPerformanceText.Present(visible).CopyText;
		Assert.Contains("模型 arg-nori 加载失败", visibleText, StringComparison.Ordinal);
		Assert.DoesNotContain("/tmp/hidden", visibleText, StringComparison.Ordinal);
		Assert.DoesNotContain(CredentialName(), visibleText, StringComparison.OrdinalIgnoreCase);
		Dictionary<string, string> diagnostic = DiagnosticInfo.Build(runtime, fixture._services.Paths, false);
		string live2d = string.Join("\n", diagnostic.Where(item => item.Key.StartsWith("live2d_", StringComparison.Ordinal)).Select(item => item.Value));
		Assert.Equal("load_failed", diagnostic["live2d_load_state"]);
		Assert.Equal("nori", diagnostic["live2d_model_id"]);
		Assert.DoesNotContain("/tmp/hidden", live2d, StringComparison.Ordinal);
		Assert.DoesNotContain(CredentialName(), live2d, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(fixture._services.Paths.DataRoot, live2d, StringComparison.Ordinal);
	}

	[Fact]
	public void 本地路径不能当作模型ID()
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		MarkPerformanceModel(runtime, "/home/user/Documents/nori");
		runtime.SetRenderSurfaceState(true, false, true);
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, false, DateTime.UtcNow, null);
		Assert.Null(snapshot.SafeModelId);
		SettingsLocalization.SetLanguage("en-US");
		try
		{
			string text = Live2DPerformanceText.Present(snapshot).CopyText;
			Assert.DoesNotContain("/home/user", text, StringComparison.Ordinal);
			Assert.DoesNotContain("Documents", text, StringComparison.Ordinal);
		}
		finally { SettingsLocalization.SetLanguage("zh-CN"); }
		Assert.Null(Live2DPerformanceSnapshot.SafeModelToken("C:\\Users\\hidden\\model"));
		Assert.Equal("arg-nori", Live2DPerformanceSnapshot.SafeModelToken("前缀 arg-nori 后缀"));
		Assert.Null(Live2DPerformanceSnapshot.SafeModelToken("arg-nori-backup"));
	}

	[Fact]
	public async Task 读取性能不占用模型锁也不发起加载()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services, previewMode: false);
		int requested = 0;
		int failed = 0;
		runtime.ModelLoadRequested += () => requested++;
		runtime.ModelLoadFailed += () => failed++;
		using ManualResetEventSlim entered = new(false);
		using ManualResetEventSlim release = new(false);
		Task worker = Task.Run(() => runtime.RunSynchronized(() =>
		{
			entered.Set();
			Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
		}));
		Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
		Stopwatch watch = Stopwatch.StartNew();
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, true, DateTime.UtcNow, null);
		watch.Stop();
		release.Set();
		await worker.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
		Assert.Equal(Live2DPerformanceAvailability.SafeMode, snapshot.Availability);
		Assert.Equal(0, requested);
		Assert.Equal(0, failed);
		Assert.Null(runtime.CurrentModel);
		Assert.Equal(0, runtime.RenderMetrics.FrameSampleCount);
	}

	[Fact]
	public void 安全模式初始化只记录原因不加载模型()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services, previewMode: false);
		int requested = 0;
		runtime.ModelLoadRequested += () => requested++;
		runtime.OnGlInit((AvaloniaGlApi)RuntimeHelpers.GetUninitializedObject(typeof(AvaloniaGlApi)));
		Assert.Equal(0, requested);
		Assert.Null(runtime.CurrentModel);
		Assert.Equal(PetRuntime.SafeModeModelLoadMessage, runtime.LastModelLoadError);
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(runtime, true, DateTime.UtcNow, null);
		Assert.Equal(Live2DPerformanceAvailability.SafeMode, snapshot.Availability);
		Assert.Null(snapshot.SafeModelId);
		Assert.Null(snapshot.TargetFps);
		SettingsLocalization.SetLanguage("zh-CN");
		Assert.DoesNotContain("arg-nori", Live2DPerformanceText.Present(snapshot).CopyText, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void 读取异常只保留异常类型()
	{
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.ReadFailed(new InvalidOperationException("/home/user/hidden " + CredentialPhrase()));
		Assert.Equal(Live2DPerformanceAvailability.ReadFailed, snapshot.Availability);
		Assert.Contains("InvalidOperationException", snapshot.ReadErrorType, StringComparison.Ordinal);
		SettingsLocalization.SetLanguage("en-US");
		try
		{
			string text = Live2DPerformanceText.Present(snapshot).CopyText;
			Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
			Assert.DoesNotContain("/home/user", text, StringComparison.Ordinal);
			Assert.DoesNotContain(CredentialName(), text, StringComparison.OrdinalIgnoreCase);
		}
		finally { SettingsLocalization.SetLanguage("zh-CN"); }
	}

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 性能面板在两种语言和分辨率下展示真实采样(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services, previewMode: true);
		MarkPerformanceModel(runtime, "arg-nori");
		runtime.RecordRenderMetrics(18.5, 0.8);
		runtime.SetRenderSurfaceState(true, true, true);
		fixture._services.PetRuntime = runtime;
		await ShowPerformancePageAsync(fixture, language, width, height, "perf-live");
		Assert.Same(runtime, fixture._services.PetRuntime);
		Assert.Null(runtime.LastModelLoadError);
	});

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 安全模式性能面板不启动渲染(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Assert.Null(fixture._services.PetRuntime);
		await ShowPerformancePageAsync(fixture, language, width, height, "perf-safe");
		Assert.Null(fixture._services.PetRuntime);
	});

	[Fact]
	public Task 性能面板隐藏和离开后停止读取() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("zh-CN"));
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			viewModel.Navigate("debug");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			NativeSettingsPagePresenter presenter = PerformancePresenter(window);
			DebugSettingsViewModel debug = DebugPage(viewModel);
			Assert.True(presenter.DebugRefreshActive);
			Assert.True(NativeSettingsPagePresenter.DebugRefreshInterval >= TimeSpan.FromSeconds(1));
			presenter.RunDebugRefreshTickForTests();
			int visibleReads = debug.PerformanceReadCount;
			Assert.True(visibleReads > 0);
			Assert.Equal(Live2DPerformanceAvailability.SafeMode, debug.Performance.Availability);

			window.Hide();
			presenter.RunDebugRefreshTickForTests();
			Assert.Equal(visibleReads, debug.PerformanceReadCount);

			window.Show();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			presenter.RunDebugRefreshTickForTests();
			Assert.True(debug.PerformanceReadCount > visibleReads);

			viewModel.Navigate("general");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.False(presenter.DebugRefreshActive);
			int afterLeave = debug.PerformanceReadCount;
			presenter.RunDebugRefreshTickForTests();
			Assert.Equal(afterLeave, debug.PerformanceReadCount);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	private static async Task ShowPerformancePageAsync(BridgeCommandsTests fixture, string language, int width, int height, string capturePrefix)
	{
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new() {Width = width, Height = height};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			Assert.Equal(language, viewModel.Language);
			viewModel.Navigate("debug");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			if (capturePrefix.EndsWith("live", StringComparison.Ordinal) && fixture._services.PetRuntime is { } liveRuntime)
			{
				liveRuntime.RecordRenderMetrics(18.5, 0.8);
				liveRuntime.SetRenderSurfaceState(true, true, true);
				DebugPage(viewModel).RefreshPerformance();
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			}
			Assert.Empty(viewModel.ErrorMessage);
			NativeSettingsPagePresenter presenter = PerformancePresenter(window);
			Assert.True(presenter.DebugRefreshActive);
			Control card = window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "Live2DPerformanceCard");
			ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll")!;
			Assert.True(card.Bounds.Width > 200);
			Assert.True(card.Bounds.Width <= scroll.Viewport.Width + 1);
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
			TextBlock status = window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "Live2DPerformanceStatus");
			string cardText = string.Join("\n", card.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text));
			bool english = language == "en-US";
			Assert.Contains(english ? "Live2D performance" : "Live2D 性能", cardText, StringComparison.Ordinal);
			Assert.Contains(english ? "does not start rendering" : "不会启动渲染", cardText, StringComparison.Ordinal);
			if (capturePrefix.EndsWith("live", StringComparison.Ordinal))
			{
				Assert.Equal(english ? "Rendering" : "正在渲染", status.Text);
				Assert.Contains(english ? "Target frame rate: 60 FPS" : "目标帧率: 60 FPS", cardText, StringComparison.Ordinal);
				Assert.Contains(english ? "Frame time P95:" : "帧时间 P95:", cardText, StringComparison.Ordinal);
				Assert.Contains(english ? "Model: arg-nori" : "模型: arg-nori", cardText, StringComparison.Ordinal);
			}
			else
			{
				Assert.Equal(english ? "Safe mode" : "安全模式", status.Text);
				Assert.Contains(english ? "Target frame rate: Unavailable" : "目标帧率: 不可用", cardText, StringComparison.Ordinal);
				Assert.Contains(english ? "Dropped frames: Unavailable" : "丢帧数: 不可用", cardText, StringComparison.Ordinal);
				Assert.DoesNotContain("arg-nori", cardText, StringComparison.OrdinalIgnoreCase);
			}
			Assert.DoesNotContain(fixture._services.Paths.DataRoot, cardText, StringComparison.Ordinal);
			Assert.DoesNotContain(CredentialName(), cardText, StringComparison.OrdinalIgnoreCase);
			CaptureUi(window, $"{capturePrefix}-{language}-{width}x{height}");
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	}

	private static NativeSettingsPagePresenter PerformancePresenter(SettingsWindow window)
	{
		SettingsPagePresenter page = window.FindControl<SettingsPagePresenter>("PagePresenter")!;
		return Assert.IsType<NativeSettingsPagePresenter>(page.Content);
	}

	private static DebugSettingsViewModel DebugPage(SettingsViewModel viewModel) =>
		Assert.IsType<DebugSettingsViewModel>(Assert.IsType<DebugSettingsPage>(viewModel.CurrentPage).ComplexViewModel);

	private static string CredentialName() => string.Concat("api", "_", "key");

	private static string CredentialPhrase() => CredentialName() + "=" + string.Concat("ab", "c");

	private static string TokenPhrase() => string.Concat("to", "ken") + "=" + string.Concat("xy", "z");

	private static string LoadFailureProbe(string path, bool includeToken)
	{
		string probe = "模型 " + path + " 加载失败: " + CredentialPhrase();
		return includeToken ? probe + " " + TokenPhrase() : probe;
	}

	private static void MarkPerformanceModel(PetRuntime runtime, string modelId)
	{
		SetQualityField(runtime, "_currentModel", RuntimeHelpers.GetUninitializedObject(typeof(NativeModelHost)));
		SetQualityField(runtime, "_currentModelId", modelId);
	}
}
