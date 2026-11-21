using System.Text;
using System.Text.Json;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class PosePlayerTests
{
	private const string Json = """
		{"Groups":[[{"Id":"TestPoseA","Link":["TestPoseLink"]},{"Id":"TestPoseB"}],[{"Id":"TestPoseC"}]]}
		""";
	private static PoseDefinition Definition(string json = Json) => PoseDefinition.Parse(Encoding.UTF8.GetBytes(json));
	[Fact]
	public void 默认时间与空组解析()
	{
		Assert.Equal(0.5f, Definition().FadeInTime);
		Assert.Equal(2, Definition().GroupCount);
		Assert.Equal(0, Definition("{\"Groups\":[[]]}").GroupCount);
		Assert.Equal(0, Definition("{\"Version\":3,\"Groups\":[]}").GroupCount);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("{\"Version\":4,\"Groups\":[]}")]
	[InlineData("{\"FadeInTime\":-1,\"Groups\":[]}")]
	[InlineData("{\"FadeInTime\":1e100,\"Groups\":[]}")]
	[InlineData("{\"Groups\":[[{\"Id\":\" \"}]]}")]
	[InlineData("{\"Groups\":[[{\"Id\":\"A\",\"Link\":null}]]}")]
	public void 损坏结构在后台拒绝(string json) => Assert.Throws<JsonException>(() => Definition(json));

	[Theory]
	[InlineData(0, 1)]
	[InlineData(0.2f, 0.8125f)]
	[InlineData(0.4f, 0.75f)]
	[InlineData(0.6f, 0.625f)]
	[InlineData(0.8f, 0.25f)]
	[InlineData(1, 0)]
	public void 交叉渐变限制透光且匹配既有轨迹(float foreground, float expected)
	{
		float background = PosePlayer.BackgroundOpacityLimit(foreground);
		Assert.Equal(expected, background, 5);
		Assert.InRange((1 - foreground) * (1 - background), 0, 0.150001f);
	}
}
