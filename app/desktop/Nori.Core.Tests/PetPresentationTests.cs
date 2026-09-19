using Nori.Core.Live2D;

namespace Nori.Core.Tests;

public sealed class PetPresentationTests
{
	[Fact]
	public void QuickChat布局与参考输入框尺寸一致()
	{
		PetQuickChatLayout layout = PetQuickChatLayout.Default;

		Assert.Equal(280, layout.ViewportWidth);
		Assert.Equal(190, layout.ViewportHeight);
		Assert.Equal(280, layout.ComposerWidth);
		Assert.Equal(46, layout.ComposerHeight);
		Assert.Equal((0d, 190d), layout.ComposerAnchor);
	}

	[Fact]
	public void 内置模型与Default别名使用专门头像预设()
	{
		PetPresentationProfile argNori = PetPresentation.ForModel("arg-nori");
		PetPresentationProfile alias = PetPresentation.ForModel("default");
		PetPresentationProfile nori = PetPresentation.ForModel("nori");
		PetPresentationProfile unknown = PetPresentation.ForModel("custom-model");

		Assert.Equal(argNori, alias);
		Assert.NotEqual(argNori, nori);
		Assert.True(unknown.CropHeightFraction > argNori.CropHeightFraction);
		Assert.True(unknown.CropWidthFraction > argNori.CropWidthFraction);
		Assert.Equal(1, unknown.CropWidthFraction);
		Assert.Equal(140, unknown.Layout.VisualCenterX);
	}

	[Fact]
	public void QuickChat裁切保留完整顶部并只显示到颈肩区域()
	{
		PetPresentationProfile profile = PetPresentation.ForModel("arg-nori");
		var geometry = new PetDrawableGeometry(new PetNormalizedRect(0.1, 0.04, 0.8, 0.9));

		PetNormalizedRect crop = profile.ResolveCrop(geometry);

		Assert.Equal(0.04, crop.Y, 10);
		Assert.Equal(0.5056, crop.Width, 10);
		Assert.Equal(0.3105, crop.Height, 10);
		Assert.True(crop.Y + crop.Height < geometry.Bounds.Y + geometry.Bounds.Height / 2);
	}

	[Theory]
	[InlineData("arg-nori", 1.8611111111111112, 0.15127959847450256, 0.025035356407734896, 0.6823996603488922, 0.7792600873690932)]
	[InlineData("nori", 1.751111111111111, 0.16079527139663696, 0.009543513283511695, 0.6856392025947571, 0.9594507061587978)]
	public void 内置模型实际Drawable范围将头像贴到输入框且完整保留耳朵(
		string modelId,
		double canvasHeight,
		double geometryX,
		double geometryY,
		double geometryWidth,
		double geometryHeight)
	{
		PetPresentationProfile profile = PetPresentation.ForModel(modelId);
		var geometry = new PetDrawableGeometry(new PetNormalizedRect(
			geometryX,
			geometryY,
			geometryWidth,
			geometryHeight));
		PetNormalizedRect crop = profile.ResolveCrop(geometry);
		double modelScale = 2 / canvasHeight;
		PetViewportProjection projection = PetPresentation.CalculateQuickChatProjection(
			canvasWidth: 1,
			canvasHeight,
			modelScaleX: modelScale,
			modelScaleY: modelScale,
			modelTranslateX: 0,
			modelTranslateY: 0,
			geometry,
			profile);
		PetViewportMapping mapping = PetViewportMapping.FromProjection(
			profile.Layout.ViewportWidth,
			profile.Layout.ViewportHeight,
			1,
			canvasHeight,
			projection,
			modelScale,
			modelScale);

		Assert.True(mapping.TryMapNormalizedRectToClient(
			crop.X,
			crop.Y,
			crop.Width,
			crop.Height,
			out PetViewportRect visible));
		Assert.Equal(6, visible.Top, 8);
		Assert.Equal(190, visible.Top + visible.Height, 8);
		Assert.Equal(82, visible.Left + visible.Width / 2, 8);
		Assert.InRange(visible.Width, 140, 160);
	}

	[Fact]
	public void 未知模型根据Drawable范围顶部对齐而非回退全身()
	{
		PetPresentationProfile profile = PetPresentation.ForModel("unknown");
		var geometry = new PetDrawableGeometry(new PetNormalizedRect(0.18, 0.08, 0.64, 0.84));

		PetNormalizedRect crop = profile.ResolveCrop(geometry);

		Assert.Equal(geometry.Bounds.Y, crop.Y, 10);
		Assert.True(crop.Height < geometry.Bounds.Height / 2);
		Assert.True(crop.Y + crop.Height < geometry.Bounds.Y + geometry.Bounds.Height);
	}

	[Fact]
	public void QuickChat眼神追踪以裁切头像中心而非全身中心归一化()
	{
		PetPresentationProfile profile = PetPresentation.ForModel("arg-nori");
		var geometry = new PetDrawableGeometry(new PetNormalizedRect(0.15, 0.025, 0.68, 0.78));
		PetNormalizedRect crop = profile.ResolveCrop(geometry);

		(double centerX, double centerY) = PetPresentation.NormalizeQuickChatLookAt(
			crop.X + crop.Width / 2,
			crop.Y + crop.Height / 2,
			geometry,
			profile);
		(double rightX, double rightY) = PetPresentation.NormalizeQuickChatLookAt(
			crop.X + crop.Width,
			crop.Y,
			geometry,
			profile);

		Assert.Equal(0, centerX, 10);
		Assert.Equal(0, centerY, 10);
		Assert.Equal(1, rightX, 10);
		Assert.Equal(1, rightY, 10);
	}

	[Theory]
	[InlineData(0.1, 144, 135)]
	[InlineData(1, 190, 180)]
	[InlineData(4, 282, 270)]
	public void QuickChat适度跟随用户缩放但保持输入框与横向锚点(
		double userScale,
		double expectedHeight,
		double expectedVisualWidth)
	{
		PetQuickChatLayout layout = PetPresentation.ScaleQuickChatLayout(
			PetQuickChatLayout.Default,
			userScale);

		Assert.Equal(280, layout.ViewportWidth);
		Assert.Equal(280, layout.ComposerWidth);
		Assert.Equal(46, layout.ComposerHeight);
		Assert.Equal(82, layout.VisualCenterX);
		Assert.Equal(expectedHeight, layout.ViewportHeight, 8);
		Assert.Equal(expectedHeight, layout.VisualBottom, 8);
		Assert.Equal(expectedVisualWidth, layout.VisualMaxWidth, 8);
	}
}
