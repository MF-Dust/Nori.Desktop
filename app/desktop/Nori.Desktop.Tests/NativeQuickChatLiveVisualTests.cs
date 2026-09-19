using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>仅按需启动真实 Win32 + OpenGL，用于验收 Quick Chat 的 Live2D 最终帧。</summary>
public partial class BridgeCommandsTests
{
	private sealed class NativeCaptureDiagnostics
	{
		private string _stage = "尚未启动";
		private long _stageStarted = Stopwatch.GetTimestamp();
		private long _copyElapsedMilliseconds = -1;
		private FileLogger? _logger;

		public void Enter(string stage)
		{
			Volatile.Write(ref _stageStarted, Stopwatch.GetTimestamp());
			Volatile.Write(ref _stage, stage);
		}

		public void AttachLogger(FileLogger logger) => Volatile.Write(ref _logger, logger);

		public void RecordCopyElapsed(TimeSpan elapsed) =>
			Volatile.Write(ref _copyElapsedMilliseconds, (long)Math.Round(elapsed.TotalMilliseconds));

		public string Describe()
		{
			string stage = Volatile.Read(ref _stage);
			TimeSpan stageElapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _stageStarted));
			long copyElapsed = Volatile.Read(ref _copyElapsedMilliseconds);
			FileLogger? logger = Volatile.Read(ref _logger);
			string recentLogs = logger is null
				? "日志器尚未创建"
				: string.Join(" | ", logger.RecentLogs()
					.TakeLast(16)
					.Select(entry => $"{entry.Level}:{entry.Message}"));
			return $"stage={stage}, stageElapsed={stageElapsed.TotalSeconds:F1}s, "
				+ $"copyElapsed={(copyElapsed >= 0 ? $"{copyElapsed}ms" : "未完成")}, logs={recentLogs}";
		}
	}

	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeQuickChatLiveVisualFactAttribute : FactAttribute
	{
		public NativeQuickChatLiveVisualFactAttribute()
		{
			if (!System.OperatingSystem.IsWindows()) Skip = "真实 Quick Chat Live2D 截图仅支持 Windows。";
			else if (Environment.GetEnvironmentVariable("NORI_CAPTURE_QUICKCHAT_NATIVE") != "1")
				Skip = "设置 NORI_CAPTURE_QUICKCHAT_NATIVE=1 执行真实 Live2D 截图。";
		}
	}

	[NativeQuickChatLiveVisualFact]
	public async Task QuickChat真实窗口能捕获Live2D最终帧()
	{
		if (!System.OperatingSystem.IsWindows()) return;
		string modelSource = Environment.GetEnvironmentVariable("NORI_QUICKCHAT_MODEL_DIR")
			?? throw new InvalidOperationException("请设置 NORI_QUICKCHAT_MODEL_DIR 指向 arg-nori 模型目录。");
		string captureDirectory = Environment.GetEnvironmentVariable("NORI_QUICKCHAT_NATIVE_CAPTURE_DIR")
			?? throw new InvalidOperationException("请设置 NORI_QUICKCHAT_NATIVE_CAPTURE_DIR 指定截图目录。");
		modelSource = ResolveArgNoriDirectory(modelSource);
		Directory.CreateDirectory(captureDirectory);

		using CancellationTokenSource loopCancellation = new(TimeSpan.FromSeconds(90));
		NativeCaptureDiagnostics diagnostics = new();
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread uiThread = new(() => RunNativeQuickChatCapture(
			modelSource,
			captureDirectory,
			loopCancellation,
			completion,
			diagnostics))
		{
			Name = "Nori Quick Chat native capture",
			IsBackground = true,
		};
		uiThread.SetApartmentState(ApartmentState.STA);
		uiThread.Start();

		try
		{
			await completion.Task.WaitAsync(loopCancellation.Token);
		}
		catch (OperationCanceledException exception) when (loopCancellation.IsCancellationRequested)
		{
			throw new TimeoutException($"原生截图超过 90 秒；{diagnostics.Describe()}", exception);
		}
		Assert.True(uiThread.Join(TimeSpan.FromSeconds(5)), "原生截图 UI 线程未按时退出。");
		Assert.True(File.Exists(Path.Combine(captureDirectory, "quick-chat-pet.png")));
		Assert.True(File.Exists(Path.Combine(captureDirectory, "quick-chat-composer.png")));
	}

	private static void RunNativeQuickChatCapture(
		string modelSource,
		string captureDirectory,
		CancellationTokenSource loopCancellation,
		TaskCompletionSource completion,
		NativeCaptureDiagnostics diagnostics)
	{
		try
		{
			diagnostics.Enter("初始化 Avalonia 平台");
			AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
			diagnostics.Enter("等待 UI 调度");
			Dispatcher.UIThread.Post(async () =>
			{
				try
				{
					await CaptureNativeQuickChatAsync(
						modelSource,
						captureDirectory,
						loopCancellation.Token,
						diagnostics);
					diagnostics.Enter("截图完成");
					completion.TrySetResult();
				}
				catch (Exception exception)
				{
					completion.TrySetException(exception);
				}
				finally
				{
					loopCancellation.Cancel();
				}
			});

			try { Dispatcher.UIThread.MainLoop(loopCancellation.Token); }
			catch (OperationCanceledException) when (loopCancellation.IsCancellationRequested) { }
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
			loopCancellation.Cancel();
		}
	}

	private static async Task CaptureNativeQuickChatAsync(
		string modelSource,
		string captureDirectory,
		CancellationToken cancellationToken,
		NativeCaptureDiagnostics diagnostics)
	{
		diagnostics.Enter("创建隔离测试夹具");
		using BridgeCommandsTests fixture = new(safeMode: true);
		diagnostics.AttachLogger(fixture._services.Logger);
		string modelTarget = fixture._services.Resources.ResourceDir(ResourceType.Live2D, "arg-nori");
		diagnostics.Enter("复制 arg-nori 模型");
		Stopwatch copyTimer = Stopwatch.StartNew();
		CopyDirectory(modelSource, modelTarget);
		diagnostics.RecordCopyElapsed(copyTimer.Elapsed);
		diagnostics.Enter("写入隔离配置");
		fixture._config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("arg-nori"));
		fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(true));

		diagnostics.Enter("创建 PetWindow 与 QuickChatController");
		PetWindow pet = new(
			WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet),
			fixture._services);
		QuickChatController controller = new(fixture._services, pet, _ => { }, _ => { });
		Nori.Desktop.Live2D.PetRuntime runtime = Assert.IsType<Nori.Desktop.Live2D.PetRuntime>(fixture._services.PetRuntime);
		int renderedFrames = 0;
		int loadRequests = 0;
		int loadFailures = 0;
		string? modelLoadFailure = null;
		runtime.FrameRendered += OnFrameRendered;
		runtime.ModelLoadRequested += OnModelLoadRequested;
		runtime.ModelLoadFailed += OnModelLoadFailed;
		try
		{
			diagnostics.Enter("显示 PetWindow");
			pet.Position = new PixelPoint(160, 100);
			pet.Show();
			pet.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(pet.UpdateLayout, DispatcherPriority.Background);
			await WaitUntilAsync(
				() => (pet.TryGetPlatformHandle()?.Handle ?? 0) != 0,
				TimeSpan.FromSeconds(5),
				cancellationToken);
			diagnostics.Enter("显示 QuickChatWindow");
			await controller.RefreshAsync();
			diagnostics.Enter("请求加载 arg-nori");
			runtime.RequestModelLoad("arg-nori");
			try
			{
				diagnostics.Enter("等待模型与最终帧");
				await WaitUntilAsync(
					() => runtime.CurrentModel is not null
						&& renderedFrames >= 4
						&& controller.Window is {IsVisible: true},
					TimeSpan.FromSeconds(25),
					cancellationToken,
					() => modelLoadFailure);
			}
			catch (TimeoutException exception)
			{
				string recentLogs = string.Join(" | ", fixture._services.Logger.RecentLogs()
					.TakeLast(16)
					.Select(entry => $"{entry.Level}:{entry.Message}"));
				throw new TimeoutException(
					$"等待 Live2D 最终帧超时；hwnd={pet.TryGetPlatformHandle()?.Handle ?? 0}, "
					+ $"visible={pet.IsVisible}, bounds={pet.Bounds}, requests={loadRequests}, failures={loadFailures}, "
					+ $"model={(runtime.CurrentModel is null ? "null" : runtime.CurrentModelId)}, frames={renderedFrames}, "
					+ $"loadError={runtime.LastModelLoadError ?? "null"}, renderVisible={runtime.RenderMetrics.Visible}, "
					+ $"logs={recentLogs}",
					exception);
			}
			diagnostics.Enter("稳定最终布局");
			controller.Reposition();
			await Task.Delay(250, cancellationToken);

			nint petHandle = pet.TryGetPlatformHandle()?.Handle ?? 0;
			nint composerHandle = controller.Window?.TryGetPlatformHandle()?.Handle ?? 0;
			Assert.NotEqual(0, petHandle);
			Assert.NotEqual(0, composerHandle);
			var screenshots = new WindowsScreenshotService();
			WindowsScreenshotRequest request = new(WindowsScreenshotFormat.Png, RequireForeground: false);
			diagnostics.Enter("捕获 PetWindow");
			Assert.True(screenshots.TryCapture(petHandle, request, out WindowsScreenshot? petFrame, out string? petError), petError);
			diagnostics.Enter("捕获 QuickChatWindow");
			Assert.True(screenshots.TryCapture(composerHandle, request, out WindowsScreenshot? composerFrame, out string? composerError), composerError);
			Assert.NotNull(petFrame);
			Assert.NotNull(composerFrame);
			diagnostics.Enter("写入截图产物");
			await File.WriteAllBytesAsync(Path.Combine(captureDirectory, "quick-chat-pet.png"), petFrame.Data, cancellationToken);
			await File.WriteAllBytesAsync(Path.Combine(captureDirectory, "quick-chat-composer.png"), composerFrame.Data, cancellationToken);
			Assert.Equal(Nori.Core.Live2D.PetPresentationMode.QuickChat, runtime.PresentationMode);
			Assert.Equal((int)Math.Round(pet.Width * pet.RenderScaling), petFrame.Width);
			Assert.Equal((int)Math.Round(pet.Height * pet.RenderScaling), petFrame.Height);
			Assert.InRange(controller.Window!.Position.Y + 14 * pet.RenderScaling
				- (pet.Position.Y + pet.Height * pet.RenderScaling), -4 * pet.RenderScaling, 0);
		}
		finally
		{
			diagnostics.Enter("清理原生窗口");
			runtime.FrameRendered -= OnFrameRendered;
			runtime.ModelLoadRequested -= OnModelLoadRequested;
			runtime.ModelLoadFailed -= OnModelLoadFailed;
			await controller.ShutdownAsync();
			pet.AllowClose = true;
			pet.Close();
		}

		void OnFrameRendered() => renderedFrames++;
		void OnModelLoadRequested() => loadRequests++;
		void OnModelLoadFailed()
		{
			loadFailures++;
			modelLoadFailure = runtime.LastModelLoadError ?? "Live2D 模型加载失败，运行时未提供详情。";
			diagnostics.Enter($"模型加载失败：{modelLoadFailure}");
		}
	}

	private static async Task WaitUntilAsync(
		Func<bool> predicate,
		TimeSpan timeout,
		CancellationToken cancellationToken,
		Func<string?>? failure = null)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (!predicate())
		{
			if (failure?.Invoke() is { } message) throw new InvalidOperationException(message);
			if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待 Live2D 模型最终帧超时。");
			await Task.Delay(50, cancellationToken);
		}
	}

	private static string ResolveArgNoriDirectory(string configured)
	{
		string fullPath = Path.GetFullPath(configured);
		if (File.Exists(Path.Combine(fullPath, "ARGNori.model3.json"))) return fullPath;
		string nested = Path.Combine(fullPath, "arg-nori");
		if (File.Exists(Path.Combine(nested, "ARGNori.model3.json"))) return nested;
		throw new DirectoryNotFoundException("NORI_QUICKCHAT_MODEL_DIR 中找不到 ARGNori.model3.json。");
	}

	private static void CopyDirectory(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(source, sourceFile);
			string targetFile = Path.Combine(target, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
			File.Copy(sourceFile, targetFile, overwrite: true);
		}
	}
}
