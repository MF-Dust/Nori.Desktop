using System.Text;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class MotionPlayerFadeAndOpacityTests
{
	private static MotionClip Clip(string meta = "") => MotionClip.Parse(Encoding.UTF8.GetBytes(
		$$"""
		{"Version":3,"Meta":{"Duration":2{{meta}}},"FadeInTime":9,"FadeOutTime":8,
		"Curves":[{"Target":"Parameter","Id":"FadeRegression","Segments":[0,1]}]}
		"""));

	[Theory]
	[InlineData("", 1, 1)]
	[InlineData(",\"FadeInTime\":0,\"FadeOutTime\":0", 0, 0)]
	[InlineData(",\"FadeInTime\":2,\"FadeOutTime\":0.5", 2, 0.5f)]
	[InlineData(",\"FadeInTime\":-1,\"FadeOutTime\":-1", 1, 1)]
	[InlineData(",\"FadeInTime\":0", 0, 1)]
	[InlineData(",\"FadeOutTime\":0", 1, 0)]
	public void 全局淡化只读取Meta且保留缺省与显式零(string meta, float fadeIn, float fadeOut)
	{
		MotionClip clip = Clip(meta);
		Assert.Equal(fadeIn, clip.FadeIn);
		Assert.Equal(fadeOut, clip.FadeOut);
		MotionCurve curve = Assert.Single(clip.Curves);
		Assert.Equal(-1, curve.FadeIn);
		Assert.Equal(-1, curve.FadeOut);
	}
}
