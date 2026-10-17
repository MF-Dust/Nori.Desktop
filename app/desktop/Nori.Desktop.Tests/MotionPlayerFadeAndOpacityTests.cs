using System.Text;
using Nori.Desktop.Live2D;
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

	[Live2DAssetsFact]
	public void MotionPlayer使用Meta全局淡化且model3可继承或覆盖()
	{
		MotionClip clip = Clip(",\"FadeInTime\":2,\"FadeOutTime\":1");
		byte[] moc = File.ReadAllBytes(PreparedModelAssetsTests.FindFixture("arg-nori", "ARGNori.moc3"));
		foreach (var (fields, fadeIn, fadeOut, early, late) in new[]
		{
			("", 2f, 1f, 0.1464466f, 0.4267767f),
			(",\"FadeInTime\":-1,\"FadeOutTime\":-1", 2f, 1f, 0.1464466f, 0.4267767f),
			(",\"FadeInTime\":0,\"FadeOutTime\":0", 0f, 0f, 1f, 1f),
			(",\"FadeInTime\":1,\"FadeOutTime\":2", 1f, 2f, 0.4267767f, 0.1464466f),
		})
		{
			ModelDefinition definition = ModelDefinition.Parse(Encoding.UTF8.GetBytes(
				$$"""{"Version":3,"FileReferences":{"Moc":"memory.moc3","Motions":{"Idle":[{"File":"memory.motion3.json"{{fields}}}] } } }"""));
			using var animation = new AnimatedModel(moc, definition,
				new Dictionary<string, MotionClip> { ["Idle_0"] = clip }) { RandomMotion = false };
			MotionPlayback playback = Assert.IsType<MotionPlayback>(animation.StartMotion("Idle", 0, MotionPriority.Normal));
			Assert.Equal(fadeIn, playback.FadeIn);
			Assert.Equal(fadeOut, playback.FadeOut);
			int parameter = animation.Model.GetParameterIndex("FadeRegression");
			animation.Update(0);
			Assert.Equal(fadeIn == 0 ? 1 : 0, animation.Model.GetParameterValue(parameter));
			animation.Model.SetParameterValue(parameter, 0);
			animation.Update(0.5f);
			Assert.Equal(early, animation.Model.GetParameterValue(parameter), 5);
			animation.Model.SetParameterValue(parameter, 0);
			animation.Update(1);
			Assert.Equal(late, animation.Model.GetParameterValue(parameter), 5);
		}
	}

	[Live2DAssetsFact]
	public void 绘制Tint提交模型与网格不透明度乘积且零模型不透明度提交零Alpha()
	{
		PreparedModelData assets = NativeGlRendererBufferTests.Assets("arg-nori");
		var gl = new RecordingGlApi { CreateResources = true };
		using var model = new NativeModel(assets.Moc.Span);
		for (int part = 0; part < model.PartCount; part++) model.SetPartOpacity(part, 0.5f);
		model.Update();
		using var renderer = new NativeGlRenderer(gl, model, assets.Textures);
		int drawable = Enumerable.Range(0, model.DrawableCount).First(index =>
			renderer.GetBindedTextureId(model.GetDrawableTextureIndex(index)) != 0 &&
			model.GetDrawableOpacity(index) is > 0 and < 1);
		model.Opacity = 0.3f;
		renderer.SetModelColor(0.4f, 0.5f, 0.6f, 0.7f);
		float drawableOpacity = model.GetDrawableOpacity(drawable);
		Assert.True(drawableOpacity > 0 && drawableOpacity < 1);

		renderer.PreDraw();
		int tintLocation = Assert.Single(gl.UniformQueries, query => query.Name == "tint").Location;
		renderer.DrawMesh(drawable);
		float expectedAlpha = 0.7f * model.Opacity * drawableOpacity;
		var firstTint = gl.VectorUniforms.Last(call => call.Location == tintLocation);
		Assert.Equal((0.4f, 0.5f, 0.6f, expectedAlpha), (firstTint.X, firstTint.Y, firstTint.Z, firstTint.W));

		model.Opacity = 0;
		renderer.DrawMesh(drawable);
		var zeroTint = gl.VectorUniforms.Last(call => call.Location == tintLocation);
		Assert.Equal((0.4f, 0.5f, 0.6f, 0), (zeroTint.X, zeroTint.Y, zeroTint.Z, zeroTint.W));
	}
}
