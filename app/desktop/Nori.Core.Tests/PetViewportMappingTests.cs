using Nori.Core.Live2D;

namespace Nori.Core.Tests;

public sealed class PetViewportMappingTests
{
	[Fact]
	public void TallModelFillsSquareViewport()
	{
		// CubismModelMatrix 使用 Unit 尺寸：1×2 画布 SetHeight(2) 后 scale=1。
		PetViewportMapping mapping = PetViewportMapping.Create(
			500, 500, 1, 2, 1, 1);

		PetViewportRect rect = mapping.ModelRect;

		Assert.Equal(125, rect.Left, 10);
		Assert.Equal(0, rect.Top, 10);
		Assert.Equal(250, rect.Width, 10);
		Assert.Equal(500, rect.Height, 10);
	}

	[Fact]
	public void WideModelIsCenteredWithLetterbox()
	{
		// 2×1 Unit 画布 SetHeight(2) 后 scale=2，宽模型再由投影缩小一半。
		PetViewportMapping mapping = PetViewportMapping.Create(
			500, 500, 2, 1, 2, 2);

		PetViewportRect rect = mapping.ModelRect;

		Assert.Equal(0, rect.Left, 10);
		Assert.Equal(125, rect.Top, 10);
		Assert.Equal(500, rect.Width, 10);
		Assert.Equal(250, rect.Height, 10);
	}

	[Fact]
	public void ClientCoordinatesRoundTripToNormalizedCanvas()
	{
		PetViewportMapping mapping = PetViewportMapping.Create(
			500, 500, 1, 2, 1, 1);

		Assert.True(mapping.TryMapClientToModel(187.5, 250, out double x, out double y));
		Assert.Equal(0.25, x, 10);
		Assert.Equal(0.5, y, 10);
	}

	[Fact]
	public void LetterboxIsOutsideModelCanvas()
	{
		PetViewportMapping mapping = PetViewportMapping.Create(
			500, 500, 2, 1, 2, 2);

		Assert.False(mapping.TryMapClientToModel(250, 50, out _, out _));
		Assert.True(mapping.TryMapClientToModel(250, 250, out double x, out double y));
		Assert.Equal(0.5, x, 10);
		Assert.Equal(0.5, y, 10);
	}

	[Fact]
	public void ProjectionTranslationParticipatesInForwardAndReverseMapping()
	{
		var projection = new PetViewportProjection(1.5, 2, -0.25, 0.3);
		PetViewportMapping mapping = PetViewportMapping.FromProjection(
			300,
			200,
			1,
			1,
			projection,
			0.8,
			0.8,
			0.1,
			-0.05);

		Assert.True(mapping.TryMapNormalizedRectToClient(0.4, 0.3, 0.2, 0.4, out PetViewportRect rect));
		Assert.True(mapping.TryMapClientToModel(
			rect.Left + rect.Width / 2,
			rect.Top + rect.Height / 2,
			out double x,
			out double y));
		Assert.Equal(0.5, x, 10);
		Assert.Equal(0.5, y, 10);
	}

	[Fact]
	public void OrdinaryFitProjectionRemainsEquivalentToLegacyCreate()
	{
		PetViewportMapping legacy = PetViewportMapping.Create(500, 500, 1, 2, 1, 1);
		PetViewportProjection projection = PetViewportMapping.CalculateFitProjection(500, 500, 1, 2);
		PetViewportMapping composed = PetViewportMapping.FromProjection(500, 500, 1, 2, projection, 1, 1);

		Assert.Equal(legacy, composed);
		Assert.Equal(new PetViewportRect(125, 0, 250, 500), composed.ModelRect);
	}

	[Fact]
	public void UnboundedPlaneMappingSupportsGlobalCursorOutsideWindow()
	{
		PetViewportMapping mapping = PetViewportMapping.FromFinalTransform(
			280,
			150,
			1,
			2,
			1,
			1,
			-0.4,
			0.25);

		Assert.True(mapping.TryMapClientToModelPlane(-40, 190, out double x, out double y));
		Assert.True(double.IsFinite(x));
		Assert.True(double.IsFinite(y));
		Assert.False(mapping.TryMapClientToModel(-40, 190, out _, out _));
		Assert.False(mapping.TryMapClientToModelPlane(double.NaN, 20, out _, out _));
	}
}
