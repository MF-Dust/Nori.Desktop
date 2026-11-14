using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Bridge;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public void 缺少自主行为配置时使用默认值且不写入数据库()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services);
		runtime.LoadConfigs();

		Assert.True(runtime.AutonomousBehaviorEnabled);
		Assert.True(runtime.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Normal, runtime.AutonomousFrequency);
		Assert.Null(fixture._config.Get(Live2DModelConfig.AutonomousBehaviorKey));
		Assert.Null(fixture._config.Get(Live2DModelConfig.AutonomousRandomKey));
		Assert.Null(fixture._config.Get(Live2DModelConfig.AutonomousFrequencyKey));
	}

	[Fact]
	public void 自主行为配置按现有类型推断读取并立即生效()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(Live2DModelConfig.AutonomousBehaviorKey, new ConfigValue.Text("0"));
		fixture._config.Set(Live2DModelConfig.AutonomousRandomKey, new ConfigValue.Text("true"));
		fixture._config.Set(Live2DModelConfig.AutonomousFrequencyKey, new ConfigValue.Text("lively"));
		PetRuntime runtime = new(fixture._services);
		runtime.LoadConfigs();

		Assert.False(runtime.AutonomousBehaviorEnabled);
		Assert.True(runtime.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Lively, runtime.AutonomousFrequency);

		runtime.ApplyConfig(Live2DModelConfig.AutonomousBehaviorKey, "1");
		runtime.ApplyConfig(Live2DModelConfig.AutonomousRandomKey, "0");
		runtime.ApplyConfig(Live2DModelConfig.AutonomousFrequencyKey, "nope");
		Assert.True(runtime.AutonomousBehaviorEnabled);
		Assert.False(runtime.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Normal, runtime.AutonomousFrequency);

		runtime.ApplyConfigDelete(Live2DModelConfig.AutonomousBehaviorKey);
		runtime.ApplyConfigDelete(Live2DModelConfig.AutonomousRandomKey);
		runtime.ApplyConfigDelete(Live2DModelConfig.AutonomousFrequencyKey);
		Assert.True(runtime.AutonomousBehaviorEnabled);
		Assert.True(runtime.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Normal, runtime.AutonomousFrequency);
	}

	[Fact]
	public async Task 自主行为命令持久化并热更新当前运行时()
	{
		using BridgeCommandsTests fixture = new();
		PetRuntime runtime = new(fixture._services);
		fixture._services.PetRuntime = runtime;
		await fixture.CreateCommands().InvokeAsync(
			new NativeModelTestSource(),
			"model_set_behavior",
			Args(new { autonomousBehavior = false, autonomousRandom = false, autonomousFrequency = "sparse" }));

		Assert.False(runtime.AutonomousBehaviorEnabled);
		Assert.False(runtime.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Sparse, runtime.AutonomousFrequency);
		Assert.False(fixture._config.GetBoolOr(Live2DModelConfig.AutonomousBehaviorKey, true));
		Assert.Equal(AutonomousPacing.Sparse, fixture._config.GetStringOr(Live2DModelConfig.AutonomousFrequencyKey, ""));

		using JsonDocument snapshot = JsonDocument.Parse(JsonSerializer.Serialize(fixture._runtime.BuildSnapshot(), BridgeJson.Options));
		JsonElement behaviors = snapshot.RootElement.GetProperty("behaviors");
		Assert.False(behaviors.GetProperty("autonomousBehavior").GetBoolean());
		Assert.False(behaviors.GetProperty("autonomousRandom").GetBoolean());
		Assert.Equal(AutonomousPacing.Sparse, behaviors.GetProperty("autonomousFrequency").GetString());

		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateCommands().InvokeAsync(
			new NativeModelTestSource(),
			"model_set_behavior",
			Args(new { autonomousFrequency = "weekly" })));
		Assert.Equal(AutonomousPacing.Sparse, runtime.AutonomousFrequency);
	}

	[Fact]
	public void 预览安全模式和隐藏窗口不启动自主动作()
	{
		using BridgeCommandsTests fixture = new();
		PetRuntime preview = new(fixture._services, previewMode: true);
		preview.SetRenderSurfaceState(false, false, true);
		Assert.True(preview.IsPreviewMode);
		Assert.True(preview.AutonomousSuspended);

		using BridgeCommandsTests safe = new(safeMode: true);
		PetRuntime safeRuntime = new(safe._services);
		safeRuntime.SetRenderSurfaceState(false, false, true);
		Assert.True(safeRuntime.AutonomousSuspended);

		PetRuntime hidden = new(fixture._services);
		hidden.SetRenderSurfaceState(false, false, true);
		hidden.SetRenderSurfaceState(false, false, false);
		Assert.True(hidden.AutonomousSuspended);
		Assert.True(hidden.AutonomousMotions.ResumeHeld);
	}

	[Fact]
	public void 释放运行时后不再保留自主动作候选()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetRuntime runtime = new(fixture._services);
		runtime.AutonomousMotions.Bind(4, [new AutonomousMotionSlot("Idle", 0, false)], []);
		runtime.OnGlDeinit();

		Assert.Equal(-1, runtime.AutonomousMotions.Generation);
		Assert.False(runtime.AutonomousMotions.Tick(new AutonomousFrame(
			10, true, true, AutonomousPacing.Normal, false, false, true, Nori.Live2D.MotionPriority.None, null, true)).HasMotion);
	}
}
