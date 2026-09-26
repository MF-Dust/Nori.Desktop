using Nori.Core.Configuration;
using Nori.Core.Live2D;

namespace Nori.Core.Tests;

public class Live2DModelConfigTests
{
	[Fact]
	public void DisplayKeys按基键成对给出每模型键与全局键()
	{
		string[] keys = Live2DModelConfig.DisplayKeys("arg-nori");
		Assert.Equal(
			["l2d_scale_arg-nori", "l2d_scale", "l2d_opacity_arg-nori", "l2d_opacity", "l2d_shadow_arg-nori", "l2d_shadow", "l2d_render_scale_arg-nori", "l2d_render_scale", "l2d_quality_mode_arg-nori", "l2d_quality_mode", "l2d_max_fps_arg-nori", "l2d_max_fps"],
			keys);
		Assert.Equal(9, Live2DModelConfig.BehaviorKeys.Count);
		Assert.Contains("l2d_click_through", Live2DModelConfig.BehaviorKeys);
		Assert.DoesNotContain("l2d_auto_blink", keys);
	}

	[Theory]
	[InlineData("high", "eco", "high")]
	[InlineData("", "eco", "eco")]
	[InlineData("", "", "adaptive")]
	public void 每模型文本优先空值回退全局(string model, string global, string expected)
	{
		Dictionary<string, ConfigValue> values = new()
		{
			["l2d_quality_mode_nori"] = new ConfigValue.Text(model),
			["l2d_quality_mode"] = new ConfigValue.Text(global),
		};
		Assert.Equal(expected, Live2DModelConfig.ReadPreferredText(values, Live2DModelConfig.QualityModeKey, "nori", "adaptive"));
	}

	[Fact]
	public void true文本按浮点1读取()
	{
		Dictionary<string, ConfigValue> values = new()
		{
			["l2d_scale_nori"] = ConfigValue.FromStorage("true"),
		};
		Assert.IsType<ConfigValue.Boolean>(values["l2d_scale_nori"]);
		Assert.Equal(1f, Live2DModelConfig.ReadFloat(values, Live2DModelConfig.ScaleKey, "nori", 2f));
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("nope", false)]
	[InlineData("", false)]
	public void 无效或空白每模型布尔回退全局(string model, bool expected)
	{
		Dictionary<string, ConfigValue> values = new()
		{
			["l2d_shadow_nori"] = model == "1" ? ConfigValue.FromStorage(model) : new ConfigValue.Text(model),
			["l2d_shadow"] = ConfigValue.FromStorage("0"),
		};
		Assert.Equal(expected, Live2DModelConfig.ReadBool(values, Live2DModelConfig.ShadowKey, "nori", true));
	}
}
