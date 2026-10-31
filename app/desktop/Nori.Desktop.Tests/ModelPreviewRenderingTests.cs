using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Live2D;
using Nori.Desktop.Models;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>独立模型预览的双实例生命周期、纹理所有权与坐标映射。</summary>
public sealed class ModelPreviewRenderingTests
{
	[Fact]
	public void 宿主与GL留在Desktop而模型动画属于独立数据层()
	{
		var desktop = typeof(PetRuntime).Assembly;
		var live2D = typeof(NativeModel).Assembly;
		Assert.NotEqual(desktop, live2D);
		Assert.Same(desktop, typeof(NativeModelHost).Assembly);
		Assert.Same(desktop, typeof(NativeGlRenderer).Assembly);
		Assert.Same(live2D, typeof(AnimatedModel).Assembly);
		Assert.Contains(desktop.GetReferencedAssemblies(), reference => reference.Name == live2D.GetName().Name);
		foreach (var assembly in new[] { desktop, live2D })
			Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
				reference => reference.Name!.StartsWith("Live2DCSharpSDK", StringComparison.Ordinal));
		Assert.DoesNotContain(live2D.GetReferencedAssemblies(), reference =>
			reference.Name == desktop.GetName().Name || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
		Assert.Equal(typeof(object), typeof(NativeModelHost).BaseType);
		Assert.Equal(typeof(object), typeof(NativeGlRenderer).BaseType);
		Assert.Equal(typeof(NativeModel), typeof(NativeModelHost).GetProperty("Model")!.PropertyType);
		Assert.Equal(typeof(AnimatedModel), typeof(NativeModelHost).GetProperty("Animation")!.PropertyType);
		Assert.Equal(typeof(AnimatedModel), typeof(Nori.Desktop.Live2D.Behaviors.BehaviorContext).GetProperty("Model")!.PropertyType);
	}

	[Fact]
	public void 同路径模型纹理由各模型独占并分别释放()
	{
		var gl = new RecordingGlApi { CreateResources = true };
		using var first = new NativeTextureOwner(gl);
		using var second = new NativeTextureOwner(gl);
		var texture = new PreparedTexture(0, "same.png", new(1, 1, [255, 255, 255, 255]));
		first.Upload(texture);
		second.Upload(texture);
		Assert.NotEqual(first[0], second[0]);
		int secondHandle = second[0];
		first.Dispose();
		Assert.Single(gl.DeletedTextures);
		Assert.DoesNotContain(secondHandle, gl.DeletedTextures);
		second.Dispose();
		Assert.Equal(gl.CreatedTextures.Order(), gl.DeletedTextures.Order());
	}

	[Fact]
	public void 归一化区域使用逻辑像素映射且保留预览缩放()
	{
		PetViewportMapping mapping = PetViewportMapping.FromFinalTransform(
			400,
			800,
			1,
			2,
			1.5,
			1.5);

		Assert.True(mapping.TryMapNormalizedRectToClient(0.25, 0.25, 0.5, 0.5, out PetViewportRect region));
		Assert.Equal(125, region.Left, 8);
		Assert.Equal(100, region.Top, 8);
		Assert.Equal(150, region.Width, 8);
		Assert.Equal(600, region.Height, 8);
		Assert.True(mapping.TryMapClientToModel(200, 400, out double x, out double y));
		Assert.Equal(0.5, x, 8);
		Assert.Equal(0.5, y, 8);
		Assert.False(mapping.TryMapNormalizedRectToClient(-0.1, 0, 0.5, 0.5, out _));
		Assert.False(mapping.TryMapNormalizedRectToClient(0, 0, double.NaN, 0.5, out _));
	}

	[Fact]
	public async Task 模型加载操作完成失败与取消都有终态()
	{
		using CancellationTokenSource completedCancellation = new();
		ModelLoadOperation completed = Operation(completedCancellation);
		completed.Complete();
		await completed.Completion;

		using CancellationTokenSource failedCancellation = new();
		ModelLoadOperation failed = Operation(failedCancellation);
		failed.Fail(new InvalidOperationException("预览失败"));
		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Completion);
		Assert.Equal("预览失败", failure.Message);

		using CancellationTokenSource canceledCancellation = new();
		ModelLoadOperation canceled = Operation(canceledCancellation);
		canceled.Invalidate();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.Completion);
	}

	private static ModelLoadOperation Operation(CancellationTokenSource cancellation) => new(
		1,
		"arg-nori",
		null,
		cancellation,
		Task.FromResult(ModelLoadOutcome.Canceled()));

}

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 同一运行时串行而宠物预览不共享全局锁()
	{
		using var fixture = new BridgeCommandsTests();
		var pet = new PetRuntime(fixture._services);
		var preview = new PetRuntime(fixture._services, previewMode: true);
		using ManualResetEventSlim entered = new(), release = new(), sameEntered = new(), previewEntered = new();
		// 阻塞锁测试使用独立线程，避免线程池饥饿被误判为运行时共享全局锁。
		Task first = Task.Factory.StartNew(() => pet.RunSynchronized(() =>
		{
			entered.Set();
			release.Wait();
		}), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		Task same = Task.CompletedTask, other = Task.CompletedTask;
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
			same = Task.Factory.StartNew(() => pet.RunSynchronized(sameEntered.Set),
				CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
			other = Task.Factory.StartNew(() => preview.RunSynchronized(previewEntered.Set),
				CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
			Assert.True(previewEntered.Wait(TimeSpan.FromSeconds(2)));
			Assert.False(sameEntered.Wait(TimeSpan.FromMilliseconds(100)));
		}
		finally
		{
			release.Set();
			await Task.WhenAll(first, same, other).WaitAsync(TimeSpan.FromSeconds(2));
		}
	}

	[Fact]
	public void QuickChat展示切换不改写用户缩放并发布布局变化()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		var runtime = new PetRuntime(fixture._services);
		runtime.UserScale = 1.75f;
		int layoutChanges = 0;
		runtime.LayoutChanged += () => layoutChanges++;

		runtime.SetQuickChatPresentation(true);
		runtime.SetQuickChatPresentation(true);

		Assert.Equal(PetPresentationMode.QuickChat, runtime.PresentationMode);
		Assert.Equal(1.75f, runtime.UserScale);
		Assert.Equal(282, runtime.QuickChatLayout.ViewportHeight, 8);
		Assert.Equal(82, runtime.QuickChatLayout.VisualCenterX);
		Assert.Equal(1, layoutChanges);

		runtime.SetQuickChatPresentation(false);
		Assert.Equal(PetPresentationMode.Ordinary, runtime.PresentationMode);
		Assert.Equal(1.75f, runtime.UserScale);
		Assert.Equal(2, layoutChanges);
	}

	[Fact]
	public void 预览运行时忽略全局模型选择热更新()
	{
		using BridgeCommandsTests fixture = new();
		fixture._config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("nori"));
		var previewRuntime = new PetRuntime(fixture._services, previewMode: true);
		long generation = previewRuntime.ModelGeneration;

		previewRuntime.ApplyConfig(ConfigStore.KeySelectedModel, "arg-nori");
		previewRuntime.ApplyConfigDelete(ConfigStore.KeySelectedModel);

		Assert.True(previewRuntime.IsPreviewMode);
		Assert.Equal(generation, previewRuntime.ModelGeneration);
		Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
	}

	[Fact]
	public Task 预览控件不替换应用级桌宠运行时() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new();
		Assert.Null(fixture._services.PetRuntime);
		using var preview = new ModelPreviewControl(fixture._services);
		Assert.Null(fixture._services.PetRuntime);
		Assert.False(preview.IsReady);
		return Task.CompletedTask;
	});
}
