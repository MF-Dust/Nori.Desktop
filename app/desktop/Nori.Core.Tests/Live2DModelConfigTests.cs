using Nori.Core.Configuration;
using Nori.Core.Live2D;

namespace Nori.Core.Tests;

public class Live2DModelConfigTests
{
	[Theory]
	[InlineData("quality", "eco", "quality")]
	[InlineData("", "eco", "adaptive")]
	[InlineData("", "", "adaptive")]
	public void 每模型文本为空时使用默认值(string model, string global, string expected)
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
	[InlineData("nope", true)]
	[InlineData("", true)]
	[InlineData("0", false)]
	public void 无效或空白每模型布尔使用默认值(string model, bool expected)
	{
		Dictionary<string, ConfigValue> values = new()
		{
			["l2d_shadow_nori"] = ConfigValue.FromStorage(model),
			["l2d_shadow"] = ConfigValue.FromStorage("0"),
		};
		Assert.Equal(expected, Live2DModelConfig.ReadBool(values, Live2DModelConfig.ShadowKey, "nori", true));
	}
}
