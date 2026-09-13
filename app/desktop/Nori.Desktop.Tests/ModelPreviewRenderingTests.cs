using System.Runtime.CompilerServices;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Live2D;
using Nori.Desktop.Models;

namespace Nori.Desktop.Tests;

/// <summary>独立模型预览的双实例生命周期、纹理所有权与坐标映射。</summary>
public sealed class ModelPreviewRenderingTests
{
	[Fact]
	public void CubismFramework引用计数避免一个预览关闭另一个实例()
	{
		int baseline = CubismFramework.ActiveLeaseCount;
		bool baselineStarted = CubismFramework.IsStarted;
		int acquired = 0;
		var allocator = new LAppAllocator();
		var option = new CubismOption {LogFunction = _ => { }, LoggingLevel = LogLevel.Off};
		try
		{
			Assert.True(CubismFramework.StartUp(allocator, option));
			acquired++;
			Assert.True(CubismFramework.StartUp(allocator, option));
			acquired++;
			Assert.Equal(baseline + 2, CubismFramework.ActiveLeaseCount);

			CubismFramework.CleanUp();
			acquired--;
			Assert.True(CubismFramework.IsStarted);
			Assert.Equal(baseline + 1, CubismFramework.ActiveLeaseCount);

			CubismFramework.CleanUp();
			acquired--;
			Assert.Equal(baselineStarted, CubismFramework.IsStarted);
			Assert.Equal(baseline, CubismFramework.ActiveLeaseCount);
		}
		finally
		{
			while (acquired-- > 0) CubismFramework.CleanUp();
		}
	}

	[Fact]
	public async Task Cubism全局操作不会在两个渲染上下文间并发()
	{
		using ManualResetEventSlim firstEntered = new();
		using ManualResetEventSlim secondAttempted = new();
		using ManualResetEventSlim secondEntered = new();
		using ManualResetEventSlim releaseFirst = new();

		Task first = Task.Run(() => CubismFramework.RunSynchronized(() =>
		{
			firstEntered.Set();
			releaseFirst.Wait();
		}));
		Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));

		Task second = Task.Run(() =>
		{
			secondAttempted.Set();
			CubismFramework.RunSynchronized(secondEntered.Set);
		});
		Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(2)));
		try
		{
			Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100)));
		}
		finally
		{
			releaseFirst.Set();
		}

		await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(secondEntered.IsSet);
	}

	[Fact]
	public void 同路径模型纹理由各模型独占并分别释放()
	{
		var app = new FakeDelegate();
		LAppModel firstModel = (LAppModel)RuntimeHelpers.GetUninitializedObject(typeof(LAppModel));
		LAppModel secondModel = (LAppModel)RuntimeHelpers.GetUninitializedObject(typeof(LAppModel));

		TextureInfo first = app.TextureManager.CreateTextureFromPngFile(firstModel, 0, "same.png");
		TextureInfo second = app.TextureManager.CreateTextureFromPngFile(secondModel, 0, "same.png");

		Assert.NotSame(first, second);
		Assert.Equal(2, app.CreatedTextures.Count);
		app.TextureManager.ReleaseTexture(first);
		Assert.True(((FakeTexture)first).Disposed);
		Assert.False(((FakeTexture)second).Disposed);
		app.TextureManager.ReleaseTexture(second);
		Assert.True(((FakeTexture)second).Disposed);
		app.Dispose();
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

	private sealed class FakeDelegate : LAppDelegate
	{
		public FakeDelegate()
		{
			InitApp();
		}

		public List<FakeTexture> CreatedTextures { get; } = [];

		public override CubismRenderer CreateRenderer(CubismModel model) => throw new NotSupportedException();

		public override TextureInfo CreateTexture(LAppModel model, int index, int width, int height, nint data)
		{
			var texture = new FakeTexture {Id = CreatedTextures.Count + 1};
			CreatedTextures.Add(texture);
			return texture;
		}

		public override TexturePixels DecodeTexture(string fileName) => new(1, 1, [255, 255, 255, 255]);
	}

	private sealed class FakeTexture : TextureInfo
	{
		public bool Disposed { get; private set; }
		public override void Dispose() => Disposed = true;
	}
}

public partial class BridgeCommandsTests
{
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
