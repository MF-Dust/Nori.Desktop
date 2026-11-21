using System.Text;
using System.Text.Json;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class PhysicsPlayerTests
{
	private const string Rig = """
		{"Input":[{"Source":{"Target":"Parameter","Id":"ParamAngleX"},"Weight":100,"Type":"X","Reflect":true}],
		"Output":[{"Destination":{"Target":"Parameter","Id":"ParamAngleY"},"VertexIndex":1,"Scale":1,"Weight":100,"Type":"Angle","Reflect":false}],
		"Vertices":[{"Position":{"X":0,"Y":0},"Mobility":0,"Delay":1,"Acceleration":0,"Radius":0},
		{"Position":{"X":0,"Y":10},"Mobility":0,"Delay":1,"Acceleration":0,"Radius":10}],
		"Normalization":{"Position":{"Minimum":-10,"Default":0,"Maximum":10},"Angle":{"Minimum":-30,"Default":0,"Maximum":30}}}
		""";

	private static PhysicsDefinition Definition(string rig = Rig, string fps = "60") => PhysicsDefinition.Parse(Encoding.UTF8.GetBytes(
		$$$$"""{"Version":3,"Meta":{"Fps":{{{{fps}}}},"EffectiveForces":{"Gravity":{"X":0,"Y":-1},"Wind":{"X":0,"Y":0}}},"PhysicsSettings":[{{{{rig}}}}]}"""));

	[Fact]
	public void 归一化保持中点与两侧区间并截断越界()
	{
		var source = new PhysicsRange(-2, 1, 4);
		var target = new PhysicsRange(-10, 2, 8);
		Assert.Equal(2, PhysicsPlayer.Normalize(1, source, target));
		Assert.Equal(-4, PhysicsPlayer.Normalize(-0.5f, source, target));
		Assert.Equal(5, PhysicsPlayer.Normalize(2.5f, source, target));
		Assert.Equal(-10, PhysicsPlayer.Normalize(-100, source, target));
		Assert.Equal(8, PhysicsPlayer.Normalize(100, source, target));
		Assert.Equal(2, PhysicsPlayer.Normalize(1, new(1, 1, 1), target));
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("{\"Version\":4}")]
	public void 损坏结构在后台解析时拒绝(string json) => Assert.Throws<JsonException>(() => PhysicsDefinition.Parse(Encoding.UTF8.GetBytes(json)));

	[Theory]
	[InlineData("\"VertexIndex\":1", "\"VertexIndex\":0")]
	[InlineData("\"VertexIndex\":1", "\"VertexIndex\":2")]
	[InlineData("\"Type\":\"Angle\"", "\"Type\":\"Unknown\"")]
	[InlineData("\"Radius\":10", "\"Radius\":-1")]
	[InlineData("\"Weight\":100", "\"Weight\":101")]
	[InlineData("\"Maximum\":10", "\"Maximum\":-20")]
	[InlineData("\"Delay\":1", "\"Delay\":1e100")]
	public void 损坏物理配置拒绝(string before, string after) => Assert.Throws<JsonException>(() => Definition(Rig.Replace(before, after)));

	[Theory]
	[InlineData("-1")]
	[InlineData("1001")]
	public void 无效物理帧率拒绝(string fps) => Assert.Throws<JsonException>(() => Definition(fps: fps));
}
