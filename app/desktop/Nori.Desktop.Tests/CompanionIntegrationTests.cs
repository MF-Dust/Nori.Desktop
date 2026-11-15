using Nori.Core.Configuration;
using Nori.Core.Focus;
using Nori.Core.Live2D;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Live2D;
using Nori.Desktop.Windows;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 专注压低自主动作并在结束后保留原配置和冷却()
	{
		_config.Set(Live2DModelConfig.AutonomousBehaviorKey, new ConfigValue.Text("1"));
		_config.Set(Live2DModelConfig.AutonomousRandomKey, new ConfigValue.Text("0"));
		_config.Set(Live2DModelConfig.AutonomousFrequencyKey, new ConfigValue.Text("sparse"));
		_config.Set("l2d_idle_animation", new ConfigValue.Text("1"));
		_config.Set("proactive_daily_greeting", new ConfigValue.Text("true"));
		_config.Set("proactive_idle_enabled", new ConfigValue.Text("true"));
		_config.Set("tts_auto_play", new ConfigValue.Text("1"));
		string behaviorBefore = _config.GetStringOr(Live2DModelConfig.AutonomousBehaviorKey, "");
		string randomBefore = _config.GetStringOr(Live2DModelConfig.AutonomousRandomKey, "");
		string frequencyBefore = _config.GetStringOr(Live2DModelConfig.AutonomousFrequencyKey, "");
		string idleBefore = _config.GetStringOr("l2d_idle_animation", "");
		string greetingBefore = _config.GetStringOr("proactive_daily_greeting", "");
		string careBefore = _config.GetStringOr("proactive_idle_enabled", "");
		string speechBefore = _config.GetStringOr("tts_auto_play", "");

		PetRuntime pet = new(_services);
		_services.PetRuntime = pet;
		pet.LoadConfigs();
		pet.SetRenderSurfaceState(true, false, true);
		pet.AutonomousMotions.Bind(
			7,
			[new AutonomousMotionSlot("Idle", 0, false), new AutonomousMotionSlot("Idle", 1, true)],
			[new AutonomousMotionSlot("Talk", 0, false)]);
		pet.AutonomousMotions.UseRandom(static _ => 0);
		Assert.True(pet.ShouldRunAutonomousTick);
		Assert.True(pet.AutonomousBehaviorEnabled);
		Assert.False(pet.AutonomousRandomEnabled);
		Assert.Equal(AutonomousPacing.Sparse, pet.AutonomousFrequency);
		Assert.True(pet.IdleAnimationEnabled);

		AutonomousDecision first = pet.DecideAutonomousMotion(100, false, true, MotionPriority.None, null);
		Assert.Equal(AutonomousActivity.Idle, first.Activity);
		Assert.Equal(MotionPriority.Idle, first.Priority);
		Assert.Equal("Idle", first.Motion?.Group);
		pet.AutonomousMotions.Commit(first.Motion!.Value, first.Priority, 100, pet.AutonomousFrequency);

		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"focus_start",
			Args(new { minutes = 25, notify = false, silentReminders = true }));
		Assert.True(pet.FocusQuiet);
		Assert.True(_runtime.Focus.Presence.Active);
		AutonomousDecision quiet = pet.DecideAutonomousMotion(110, speaking: true, motionFinished: true, MotionPriority.None, null);
		Assert.Equal(AutonomousActivity.Suspended, quiet.Activity);
		Assert.False(quiet.HasMotion);
		Assert.False(FocusCompanionPolicy.AllowsAmbientMotion(pet.IdleAnimationEnabled, pet.FocusQuiet));
		Assert.True(AnimatedModel.ShouldStopIdle("Idle", MotionPriority.Force));
		Assert.False(AnimatedModel.ShouldStopIdle("IdleLoop", MotionPriority.Force));
		Assert.False(AnimatedModel.ShouldStopIdle("TapBody", MotionPriority.Force));
		Assert.True(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: true, safeMode: false));

		await CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "focus_end", Args(new { }));
		Assert.False(pet.FocusQuiet);
		Assert.False(_runtime.Focus.Presence.Active);
		Assert.False(pet.DecideAutonomousMotion(110, false, true, MotionPriority.None, null).HasMotion);
		AutonomousDecision resumed = pet.DecideAutonomousMotion(116, false, true, MotionPriority.None, null);
		Assert.True(resumed.HasMotion);
		Assert.Equal(MotionPriority.Idle, resumed.Priority);

		AutonomousDecision blocked = pet.DecideAutonomousMotion(200, speaking: true, motionFinished: false, MotionPriority.Force, "TapBody");
		Assert.Equal(AutonomousActivity.Interacting, blocked.Activity);
		Assert.False(blocked.HasMotion);
		AutonomousDecision voice = pet.DecideAutonomousMotion(200, speaking: true, motionFinished: false, MotionPriority.Idle, "Idle");
		Assert.Equal(AutonomousActivity.Speaking, voice.Activity);
		Assert.Equal(MotionPriority.Normal, voice.Priority);
		Assert.Equal("Talk", voice.Motion?.Group);

		Assert.Equal(behaviorBefore, _config.GetStringOr(Live2DModelConfig.AutonomousBehaviorKey, ""));
		Assert.Equal(randomBefore, _config.GetStringOr(Live2DModelConfig.AutonomousRandomKey, ""));
		Assert.Equal(frequencyBefore, _config.GetStringOr(Live2DModelConfig.AutonomousFrequencyKey, ""));
		Assert.Equal(idleBefore, _config.GetStringOr("l2d_idle_animation", ""));
		Assert.Equal(greetingBefore, _config.GetStringOr("proactive_daily_greeting", ""));
		Assert.Equal(careBefore, _config.GetStringOr("proactive_idle_enabled", ""));
		Assert.Equal(speechBefore, _config.GetStringOr("tts_auto_play", ""));
		Assert.True(pet.AutonomousBehaviorEnabled);
		Assert.Equal(AutonomousPacing.Sparse, pet.AutonomousFrequency);
		Assert.True(pet.IdleAnimationEnabled);
	}

	[Fact]
	public void 隐藏期间的专注不会清掉恢复标记且释放模型后不再留下旧样本()
	{
		PetRuntime pet = new(_services);
		pet.LoadConfigs();
		pet.SetRenderSurfaceState(true, false, true);
		pet.AutonomousMotions.Bind(3, [new AutonomousMotionSlot("Idle", 0, false)], [new AutonomousMotionSlot("Talk", 0, false)]);
		pet.ResetRenderSampleWindow(true, "nori");
		pet.RecordRenderMetrics(40, 4);
		Assert.Equal(40, pet.RenderMetrics.FrameTimeP95Ms);
		Assert.Equal("nori", pet.RenderMetrics.ModelId);

		pet.SetRenderSurfaceState(true, false, false);
		Assert.True(pet.AutonomousMotions.ResumeHeld);
		Assert.False(pet.ShouldRunAutonomousTick);
		pet.FocusQuiet = true;
		AutonomousDecision hidden = pet.DecideAutonomousMotion(20, false, true, MotionPriority.None, null);
		Assert.Equal(AutonomousActivity.Suspended, hidden.Activity);
		Assert.True(pet.AutonomousMotions.ResumeHeld);

		pet.SetRenderSurfaceState(true, false, true);
		Assert.True(pet.ShouldRunAutonomousTick);
		Assert.True(pet.AutonomousMotions.ResumeHeld);
		pet.FocusQuiet = false;
		AutonomousDecision shown = pet.DecideAutonomousMotion(20, false, true, MotionPriority.None, null);
		Assert.False(pet.AutonomousMotions.ResumeHeld);
		Assert.False(shown.HasMotion);

		pet.FocusQuiet = true;
		pet.OnGlDeinit();
		Assert.Equal(-1, pet.AutonomousMotions.Generation);
		Assert.False(pet.DecideAutonomousMotion(30, speaking: true, motionFinished: true, MotionPriority.None, null).HasMotion);
		Assert.Equal(0, pet.RenderMetrics.FrameSampleCount);
		Assert.Equal(0, pet.RenderMetrics.MaskSampleCount);
		Assert.Equal("", pet.RenderMetrics.ModelId);
		Assert.Null(pet.RenderMetrics.LastFrameSampleUtc);
		Assert.True(pet.FocusQuiet);
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(pet, false, null);
		Assert.Equal(Live2DPerformanceAvailability.Unavailable, snapshot.Availability);
		Assert.Null(snapshot.FrameTimeP95Ms);
		Assert.Null(snapshot.SafeModelId);
		Assert.Equal("unavailable", snapshot.ToDiagnosticValues()["live2d_frame_time_p95_ms"]);
	}

	[Fact]
	public async Task 重建伴侣运行时会接上已经开始的专注()
	{
		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"focus_start",
			Args(new { minutes = 25, notify = false, silentReminders = true }));
		PetRuntime late = new(_services);
		_services.PetRuntime = late;
		Assert.False(late.FocusQuiet);
		_runtime.ApplyFocusQuiet();
		Assert.True(late.FocusQuiet);
		Assert.True(late.AutonomousBehaviorEnabled);

		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new();
			fixture._config.Set(Live2DModelConfig.AutonomousBehaviorKey, new ConfigValue.Text("0"));
			fixture._config.Set(Live2DModelConfig.AutonomousFrequencyKey, new ConfigValue.Text("lively"));
			string behaviorBefore = fixture._config.GetStringOr(Live2DModelConfig.AutonomousBehaviorKey, "");
			string frequencyBefore = fixture._config.GetStringOr(Live2DModelConfig.AutonomousFrequencyKey, "");
			await fixture.CreateCommands().InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main),
				"focus_start",
				Args(new { minutes = 25, notify = false, silentReminders = true }));
			WindowDefinition definition = WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet);
			PetWindow window = new(definition, fixture._services);
			try
			{
				Assert.NotNull(fixture._services.PetRuntime);
				fixture._services.PetRuntime.LoadConfigs();
				Assert.True(fixture._services.PetRuntime.FocusQuiet);
				Assert.False(fixture._services.PetRuntime.AutonomousBehaviorEnabled);
				Assert.Equal(AutonomousPacing.Lively, fixture._services.PetRuntime.AutonomousFrequency);
				window.AllowClose = true;
			}
			finally
			{
				window.Close();
			}
			Assert.Equal(behaviorBefore, fixture._config.GetStringOr(Live2DModelConfig.AutonomousBehaviorKey, ""));
			Assert.Equal(frequencyBefore, fixture._config.GetStringOr(Live2DModelConfig.AutonomousFrequencyKey, ""));
		});
	}

	[Fact]
	public async Task 安全模式读性能和不开始专注都不会创建伴侣运行时()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Assert.Null(fixture._services.PetRuntime);
		await fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "focus_view", Args(new { }));
		Live2DPerformanceSnapshot snapshot = Live2DPerformanceSnapshot.Capture(fixture._services.PetRuntime, true, null);
		Assert.Null(fixture._services.PetRuntime);
		Assert.Equal(Live2DPerformanceAvailability.SafeMode, snapshot.Availability);
		Assert.Null(snapshot.FrameTimeP95Ms);
		Assert.Null(snapshot.TargetFps);
		Assert.Null(snapshot.SafeModelId);
		Assert.Equal("unavailable", snapshot.ToDiagnosticValues()["live2d_effective_fps"]);

		PetRuntime runtime = new(fixture._services);
		runtime.SetRenderSurfaceState(true, true, true);
		runtime.AutonomousMotions.Bind(1, [new AutonomousMotionSlot("Idle", 0, false)], []);
		Assert.True(runtime.AutonomousSuspended);
		Assert.False(runtime.ShouldRunAutonomousTick);
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"focus_start",
			Args(new { minutes = 25 })));
		Assert.False(fixture._services.PetRuntime?.FocusQuiet ?? false);
	}

	[Fact]
	public async Task 性能样本窗口有上界且切换模型时不把旧样本配到新身份()
	{
		PetRuntime runtime = new(_services, previewMode: true);
		runtime.RecordRenderMetrics(1, 0.1);
		Assert.Equal(1, runtime.RenderMetrics.FrameSampleCount);
		for (int index = 0; index < 5_000; index++)
			runtime.RecordRenderMetrics(10 + (index % 7), 0.2 + (index % 3));
		int capped = runtime.RenderMetrics.FrameSampleCount;
		int cappedMasks = runtime.RenderMetrics.MaskSampleCount;
		Assert.InRange(capped, 2, 4_000);
		Assert.Equal(capped, cappedMasks);
		for (int index = 0; index < 1_000; index++)
			runtime.RecordRenderMetrics(3, 0.2);
		Assert.Equal(capped, runtime.RenderMetrics.FrameSampleCount);
		Assert.Equal(cappedMasks, runtime.RenderMetrics.MaskSampleCount);

		int torn = 0;
		Task reader = Task.Run(() =>
		{
			for (int index = 0; index < 2_000; index++)
			{
				PetRenderMetrics metrics = runtime.RenderMetrics;
				bool noriHasOther = metrics.ModelId == "nori"
					&& metrics.FrameSampleCount > 0
					&& Math.Abs(metrics.FrameTimeP95Ms - 10) < 0.01;
				bool argHasOther = metrics.ModelId == "arg-nori"
					&& metrics.FrameSampleCount > 0
					&& Math.Abs(metrics.FrameTimeP95Ms - 50) < 0.01;
				if (noriHasOther || argHasOther) Interlocked.Increment(ref torn);
			}
		});
		for (int index = 0; index < 200; index++)
		{
			runtime.ResetRenderSampleWindow(true, "nori");
			runtime.RecordRenderMetrics(50, 5);
			runtime.ResetRenderSampleWindow(true, "arg-nori");
			runtime.RecordRenderMetrics(10, 1);
		}
		await reader.WaitAsync(TimeSpan.FromSeconds(8));
		Assert.Equal(0, torn);
		PetRenderMetrics finalMetrics = runtime.RenderMetrics;
		Assert.Equal("arg-nori", finalMetrics.ModelId);
		Assert.Equal(10, finalMetrics.FrameTimeP95Ms);
		Assert.InRange(finalMetrics.FrameSampleCount, 1, capped);

		Task[] pressure =
		[
			Task.Run(() =>
			{
				for (int index = 0; index < 4_000; index++)
					runtime.RecordRenderMetrics(16 + (index % 5), 1);
			}),
			Task.Run(() =>
			{
				for (int index = 0; index < 4_000; index++)
					_ = runtime.RenderMetrics;
			}),
		];
		await Task.WhenAll(pressure).WaitAsync(TimeSpan.FromSeconds(8));
	}
}
