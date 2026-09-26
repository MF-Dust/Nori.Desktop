using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public void LoadConfigs一次批量读取并保留每模型回退与类型推断()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set("l2d_scale_arg-nori", new ConfigValue.Text("1.25"));
		fixture._config.Set("l2d_scale", new ConfigValue.Text("2"));
		fixture._config.Set("l2d_opacity", new ConfigValue.Text("0.5"));
		fixture._config.Set("l2d_shadow_arg-nori", new ConfigValue.Text("0"));
		fixture._config.Set("l2d_shadow", new ConfigValue.Text("1"));
		fixture._config.Set("l2d_render_scale_arg-nori", new ConfigValue.Text("1.25"));
		fixture._config.Set("l2d_quality_mode_arg-nori", new ConfigValue.Text("eco"));
		fixture._config.Set("l2d_quality_mode", new ConfigValue.Text("quality"));
		fixture._config.Set("l2d_max_fps", new ConfigValue.Text("45"));
		fixture._config.Set("l2d_click_through", new ConfigValue.Text("1"));
		fixture._config.Set("l2d_beat_sync", new ConfigValue.Text("true"));
		fixture._config.Set("l2d_expression_enabled", new ConfigValue.Text("false"));
		fixture._config.Set("l2d_eye_tracking", new ConfigValue.Text("0"));
		fixture._config.Set("l2d_idle_animation", new ConfigValue.Text("0"));
		fixture._config.Set("l2d_click_interaction", new ConfigValue.Text("false"));
		PetInteractionConfig interaction = new()
		{
			Regions =
			[
				new PetInteractionRegion
				{
					Id = "head",
					Name = "头部",
					Rect = new PetInteractionRect {X = 0.2, Y = 0.1, Width = 0.3, Height = 0.3},
				},
			],
		};
		fixture._config.Set(PetInteractionConfig.StorageKey("arg-nori"), new ConfigValue.Json(interaction.ToJsonNode()));

		PetRuntime runtime = new(fixture._services);
		int queryCount = 0;
		fixture._config.ReadQueryExecuted = () => queryCount++;
		try
		{
			runtime.LoadConfigs();
			Assert.Equal(1, queryCount);
			Assert.Equal(1.25f, runtime.UserScale);
			Assert.Equal(0.5f, runtime.Opacity);
			Assert.False(runtime.ShadowEnabled);
			Assert.Equal(1.25f, runtime.RenderScale);
			Assert.Equal("eco", runtime.QualityMode);
			Assert.Equal(45, runtime.MaxFps);
			Assert.True(runtime.ClickThroughEnabled);
			Assert.True(runtime.BeatSyncEnabled);
			Assert.False(runtime.ExpressionEnabled);
			Assert.False(runtime.EyeTrackingEnabled);
			Assert.False(runtime.IdleAnimationEnabled);
			Assert.False(runtime.ClickInteraction);
			Assert.True(runtime.AutoBlinkEnabled);
			Assert.True(runtime.LipSyncEnabled);
			Assert.Equal("head", Assert.Single(runtime.InteractionConfig.Regions).Id);

			fixture._config.Set("l2d_scale_arg-nori", new ConfigValue.Text("nope"));
			queryCount = 0;
			runtime.LoadConfigs();
			Assert.Equal(1, queryCount);
			Assert.Equal(2f, runtime.UserScale);
		}
		finally
		{
			fixture._config.ReadQueryExecuted = null;
		}
	}
}
