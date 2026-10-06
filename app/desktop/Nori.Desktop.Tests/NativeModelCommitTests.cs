using System.Reflection;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[NativeGlAssetsTheory]
	[InlineData(2)]
	[InlineData(3)]
	public async Task 原生候选失败和过期准备不替换已提交模型(int version)
	{
		PreparedModel? prepared = null;
		await WithSettingsUiAsync(async () =>
		{
			prepared = await Task.Run(() => ModelPreparation.PrepareAsync("arg-nori",
				Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture("arg-nori", "ARGNori.model3.json"))!, 1, CancellationToken.None));
		});
		using var fixture = new BridgeCommandsTests(safeMode: true);
		var runtime = new PetRuntime(fixture._services, previewMode: true) { IdleAnimationEnabled = false };
		using var context = new AngleTestContext(version);
		context.MakeCurrent(0);
		var gl = context.CreateApi();
		try
		{
			runtime.OnGlInit(gl);
			ModelLoadOperation first = InstallOperation(runtime, prepared!);
			runtime.RunSynchronized(() => runtime.RenderFrame(0.016f, 720, 480));
			Assert.True(first.Completion.IsCompletedSuccessfully);
			NativeModelHost original = Assert.IsType<NativeModelHost>(runtime.CurrentModel);
			byte[] before = NativeGlHarnessTests.DrawAndRead(original, gl, 720, 480);

			PreparedModel invalid = prepared! with
			{
				ModelId = "nori",
				Generation = 2,
				Assets = prepared!.Assets with { Textures = [new(0, "候选损坏.png", new(1, 1, []))] },
			};
			ModelLoadOperation failed = InstallOperation(runtime, invalid);
			runtime.RunSynchronized(() => runtime.RenderFrame(0.016f, 720, 480));
			Assert.True(failed.Completion.IsFaulted);
			Assert.IsType<InvalidOperationException>(failed.Completion.Exception!.InnerException);
			Assert.Same(original, runtime.CurrentModel);
			Assert.Equal("arg-nori", runtime.CurrentModelId);
			Assert.False(original.Model.IsDisposed);
			Assert.NotNull(runtime.LastModelLoadError);
			Assert.Equal(0, context.GetError());

			// 操作世代过期时不能创建或提交准备结果，也不能影响旧模型可绘制性。
			ModelLoadOperation stale = InstallOperation(runtime, prepared! with { Generation = 3 });
			stale.Invalidate();
			runtime.RunSynchronized(() => runtime.RenderFrame(0.016f, 720, 480));
			Assert.True(stale.Completion.IsCanceled);
			Assert.Same(original, runtime.CurrentModel);
			Assert.Equal(0, context.GetError());
			Assert.Contains(before.Where((_, index) => index % 4 == 3), alpha => alpha != 0);
			Assert.Contains(NativeGlHarnessTests.DrawAndRead(original, gl, 720, 480).Where((_, index) => index % 4 == 3), alpha => alpha != 0);
		}
		finally { runtime.RunSynchronized(runtime.OnGlDeinit); }
	}

	[NativeGlAssetsTheory]
	[InlineData(2)]
	[InlineData(3)]
	public async Task 切换模型清除旧眨眼和指针来源(int version)
	{
		PreparedModel prepared = await Task.Run(() => ModelPreparation.PrepareAsync("arg-nori",
			Path.GetDirectoryName(PreparedModelAssetsTests.FindFixture("arg-nori", "ARGNori.model3.json"))!, 1, CancellationToken.None));
		using var fixture = new BridgeCommandsTests(safeMode: true);
		var runtime = new PetRuntime(fixture._services, previewMode: true) { IdleAnimationEnabled = false };
		using var context = new AngleTestContext(version);
		context.MakeCurrent(0);
		try
		{
			runtime.OnGlInit(context.CreateApi());
			InstallOperation(runtime, prepared);
			runtime.RunSynchronized(() => runtime.RenderFrame(0.016f, 720, 480));
			NativeModelHost original = Assert.IsType<NativeModelHost>(runtime.CurrentModel);
			runtime.LookAt(360, 240, 720, 480);
			Assert.True(original.HasPointerInput);

			var blink = (Live2D.Behaviors.AutoBlinkBehavior)typeof(PetRuntime)
				.GetField("_autoBlink", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
			typeof(Live2D.Behaviors.AutoBlinkBehavior)
				.GetField("_delaySeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(blink, 0d);
			var parameters = new Live2D.Behaviors.ModelParameters();
			parameters.BindModel(original.Model);
			original.Model.SetParameterValue(parameters.LeftEyeOpenIndex, 0.8f);
			original.Model.SetParameterValue(parameters.RightEyeOpenIndex, 0.8f);
			blink.Execute(new Live2D.Behaviors.BehaviorContext
			{
				Model = original.Animation,
				ModelParameters = parameters,
				IsIdleMotion = true,
				TimeDelta = 0.016,
			});
			FieldInfo phase = typeof(Live2D.Behaviors.AutoBlinkBehavior)
				.GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)!;
			Assert.Equal("Closing", phase.GetValue(blink)!.ToString());

			InstallOperation(runtime, prepared with { Generation = 2 });
			runtime.RunSynchronized(() => runtime.RenderFrame(0.016f, 720, 480));
			NativeModelHost replacement = Assert.IsType<NativeModelHost>(runtime.CurrentModel);
			Assert.NotSame(original, replacement);
			Assert.False(replacement.HasPointerInput);
			Assert.Equal("Idle", phase.GetValue(blink)!.ToString());
			Assert.Equal(0, context.GetError());
		}
		finally { runtime.RunSynchronized(runtime.OnGlDeinit); }
	}

	private static ModelLoadOperation InstallOperation(PetRuntime runtime, PreparedModel prepared)
	{
		var operation = new ModelLoadOperation(prepared.Generation, prepared.ModelId, runtime.CurrentModelId,
			new CancellationTokenSource(), Task.FromResult(ModelLoadOutcome.Succeeded(prepared)));
		typeof(PetRuntime).GetField("_modelGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, prepared.Generation);
		typeof(PetRuntime).GetField("_pendingModelLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, operation);
		return operation;
	}
}
