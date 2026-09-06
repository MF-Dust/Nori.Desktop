using Nori.Core.Live2D;
using Xunit;

namespace Nori.Core.Tests;

public class PetSizingTests
{
	[Fact]
	public void InRangeModelSize_PreservesExactSize()
	{
		var (width, height) = PetSizing.CalculateSafeBaseSize(400, 520);
		Assert.Equal(400, width);
		Assert.Equal(520, height);
	}

	[Fact]
	public void SquareLargeModel_ScalesToSquareSafeSize()
	{
		var (width, height) = PetSizing.CalculateSafeBaseSize(2048, 2048);
		Assert.True(width <= PetSizing.MaxPetBaseWidth);
		Assert.True(height <= PetSizing.MaxPetBaseHeight);
		Assert.Equal(520, width);
		Assert.Equal(520, height);
	}

	[Fact]
	public void TallModel_ScalesProportionally()
	{
		var (width, height) = PetSizing.CalculateSafeBaseSize(2048, 4096);
		Assert.Equal(260, width);
		Assert.Equal(520, height);
	}

	[Fact]
	public void WideModel_StaysWithinSafeBounds()
	{
		var (width, height) = PetSizing.CalculateSafeBaseSize(4096, 2048);
		Assert.True(width <= PetSizing.MaxPetBaseWidth);
		Assert.True(height >= PetSizing.MinPetBaseHeight);
		Assert.True(height <= PetSizing.MaxPetBaseHeight);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(-100, 500)]
	[InlineData(double.NaN, 500)]
	[InlineData(400, double.PositiveInfinity)]
	public void InvalidDimensions_FallbackToDefaultSize(double w, double h)
	{
		var (width, height) = PetSizing.CalculateSafeBaseSize(w, h);
		Assert.Equal(PetSizing.DefaultPetWidth, width);
		Assert.Equal(PetSizing.DefaultPetHeight, height);
	}

	[Theory]
	[InlineData(0.5)]
	[InlineData(1.0)]
	[InlineData(1.5)]
	[InlineData(4.0)]
	public void WindowSize_ScalesWithUserScale_OnLargeScreen(double userScale)
	{
		// On a large screen the requested display scale maps directly to the pet window.
		var (width, height) = PetSizing.CalculateWindowSize(400, 520, userScale, 3840, 2160, 1.0);
		Assert.Equal((int)Math.Round(400 * userScale), width);
		Assert.Equal((int)Math.Round(520 * userScale), height);
	}

	[Fact]
	public void WindowSize_ClampedByMinimum80Px()
	{
		var (width, height) = PetSizing.CalculateWindowSize(400, 520, 0.1, 1920, 1080, 1.0);
		Assert.Equal(80, width); // 40 clamped to 80
		Assert.Equal(80, height); // 52 clamped to 80
	}

	[Fact]
	public void WindowSize_AllowsFourHundredPercentForDefaultPet()
	{
		var (width, height) = PetSizing.CalculateWindowSize(400, 520, 4.0, 1920, 1080, 1.0);
		Assert.Equal(1600, width);
		Assert.Equal(2080, height);
	}
}
