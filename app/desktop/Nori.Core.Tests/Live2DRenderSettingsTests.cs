using Nori.Core.Live2D;

namespace Nori.Core.Tests;

public sealed class Live2DRenderSettingsTests
{
	[Fact]
	public void InvalidValuesAreNormalizedToSafeNativeRanges()
	{
		Live2DRenderSettings settings = Live2DRenderSettings.Normalize(
			" arg-nori ",
			opacity: 4,
			renderScale: 0.1f,
			qualityMode: "not-a-mode",
			maxFps: 999);

		Assert.Equal("arg-nori", settings.ModelId);
		Assert.Equal(1.0f, settings.Opacity);
		Assert.Equal(0.5f, settings.RenderScale);
		Assert.Equal(Live2DQualityMode.Adaptive, settings.QualityMode);
		Assert.Equal(240, settings.MaxFps);
	}

	[Fact]
	public void RenderScaleSupportsFourXAndClampsAboveIt()
	{
		Assert.Equal(4.0f, Live2DRenderSettings.Normalize("nori", renderScale: 4.0f).RenderScale);
		Assert.Equal(4.0f, Live2DRenderSettings.Normalize("nori", renderScale: 8.0f).RenderScale);
	}

	[Fact]
	public void NativeSliceContainsOnlyTheTwoExistingModelIds()
	{
		Assert.Equal(["arg-nori", "nori"], SupportedModelIds.All);
		Assert.Equal("arg-nori", SupportedModelIds.Normalize("ARG-NORI"));
		Assert.Equal("nori", SupportedModelIds.Normalize("NORI"));
		Assert.Null(SupportedModelIds.Normalize("imported-model"));
	}

	[Fact]
	public void QualityModeStorageIsCanonical()
	{
		Assert.Equal("adaptive", Live2DRenderSettings.QualityModeToStorage(Live2DQualityMode.Adaptive));
		Assert.Equal("quality", Live2DRenderSettings.QualityModeToStorage(Live2DQualityMode.Quality));
		Assert.Equal("eco", Live2DRenderSettings.QualityModeToStorage(Live2DQualityMode.Eco));
	}
}
