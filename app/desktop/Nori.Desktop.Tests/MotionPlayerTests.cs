using System.Text;
using System.Text.Json;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class MotionPlayerTests
{
	private static MotionClip Clip(string curves, string extra = "", string meta = "") => MotionClip.Parse(Encoding.UTF8.GetBytes(
		$$"""{"Version":3,"Meta":{"Duration":2,"FadeInTime":0,"FadeOutTime":0{{meta}}},"Curves":[{{curves}}]{{extra}}} """));

	private static string Curve(string segments, string id = "Test", string target = "Parameter", string extra = "") =>
		$$"""{"Target":"{{target}}","Id":"{{id}}","Segments":[{{segments}}]{{extra}}} """;

	[Theory]
	[InlineData("0,0,0,1,10", 0.25f, 2.5f)]
	[InlineData("0,0,2,1,10", 0.25f, 0)]
	[InlineData("0,0,3,1,10", 0.25f, 10)]
	[InlineData("0,0,2,1,10,0,2,20", 1, 10)]
	[InlineData("0,0,0,1,10", 2, 10)]
	[InlineData("0.2,7", 0, 7)]
	public void 基础曲线与段边界(string segments, float time, float expected) =>
		Assert.Equal(expected, Assert.Single(Clip(Curve(segments)).Curves).Evaluate(time), 5);

	[Fact]
	public void 非限制贝塞尔先反解时间而非直接代入()
	{
		// x(u)=u³，y(u)=3u(1-u)；x=1/8 时 u=1/2，y=3/4。
		MotionCurve curve = Assert.Single(Clip(Curve("0,0,1,0,1,0,1,1,0")).Curves);
		Assert.Equal(0.75f, curve.Evaluate(0.125f), 5);
		Assert.Equal(0, curve.Evaluate(0), 5);
		Assert.Equal(0, curve.Evaluate(1), 5);
		MotionCurve restricted = Assert.Single(Clip(Curve("0,0,1,0.3333333,1,0.6666667,1,1,0"), meta: ",\"AreBeziersRestricted\":true").Curves);
		Assert.Equal(0.75f, restricted.Evaluate(0.5f), 5);
	}

	[Theory]
	[InlineData("0,0,4,1,1")]
	[InlineData("0,0,1,0.1,1")]
	[InlineData("0,0,0,0,1")]
	[InlineData("0,0,0,3,1")]
	[InlineData("0,0,1,-1,0,0.8,1,1,1")]
	[InlineData("0,0,0,1,1e100")]
	public void 损坏曲线在后台解析时拒绝(string segments) => Assert.Throws<JsonException>(() => Clip(Curve(segments)));

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("{\"Version\":3,\"Meta\":{\"Duration\":0},\"Curves\":[]}")]
	public void 损坏结构报告JSON异常(string json) => Assert.Throws<JsonException>(() => MotionClip.Parse(Encoding.UTF8.GetBytes(json)));
}
