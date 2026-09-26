using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

public sealed class PetHitMaskTests
{
	[Fact]
	public void SourcePixelsKeepTopAndBottomOrientation()
	{
		byte[] topPixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(topPixels, PetHitMask.Width, PetHitMask.Height, 48, 0);

		PetHitMask.Bounds topBounds = PetHitMask.BuildFromSourcePixels(topPixels, PetHitMask.Width, PetHitMask.Height);

		Assert.True(PetHitMask.IsPointOnModel(topBounds, 48.5, 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(topBounds, 48.5, PetHitMask.Height - 0.5, PetHitMask.Width, PetHitMask.Height));

		byte[] bottomPixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(bottomPixels, PetHitMask.Width, PetHitMask.Height, 48, PetHitMask.Height - 1);
		PetHitMask.Bounds bottomBounds = PetHitMask.BuildFromSourcePixels(bottomPixels, PetHitMask.Width, PetHitMask.Height);

		Assert.False(PetHitMask.IsPointOnModel(bottomBounds, 48.5, 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.True(PetHitMask.IsPointOnModel(bottomBounds, 48.5, PetHitMask.Height - 0.5, PetHitMask.Width, PetHitMask.Height));
	}

	[Fact]
	public void ReducedPixelsKeepTopAndBottomOrientation()
	{
		byte[] topPixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(topPixels, PetHitMask.Width, PetHitMask.Height, 48, 0);

		PetHitMask.Bounds topBounds = PetHitMask.BuildFromReducedPixels(topPixels, PetHitMask.Width, PetHitMask.Height);

		Assert.True(PetHitMask.IsPointOnModel(topBounds, 48.5, 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(topBounds, 48.5, PetHitMask.Height - 0.5, PetHitMask.Width, PetHitMask.Height));

		byte[] bottomPixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(bottomPixels, PetHitMask.Width, PetHitMask.Height, 48, PetHitMask.Height - 1);
		PetHitMask.Bounds bottomBounds = PetHitMask.BuildFromReducedPixels(bottomPixels, PetHitMask.Width, PetHitMask.Height);

		Assert.False(PetHitMask.IsPointOnModel(bottomBounds, 48.5, 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.True(PetHitMask.IsPointOnModel(bottomBounds, 48.5, PetHitMask.Height - 0.5, PetHitMask.Width, PetHitMask.Height));
	}

	[Fact]
	public void SourcePixelsDetectSamplesAtEachPointInsideCell()
	{
		foreach ((double xFraction, double yFraction) in new[]
		{
			(0.2, 0.2),
			(0.8, 0.2),
			(0.2, 0.8),
			(0.8, 0.8),
			(0.5, 0.5),
		})
		{
			const int sourceWidth = 960;
			const int sourceHeight = 1280;
			const int column = 17;
			const int row = 29;
			byte[] pixels = CreatePixels(sourceWidth, sourceHeight);
			SetAlphaAtCellSample(pixels, sourceWidth, sourceHeight, column, row, xFraction, yFraction);
			PetHitMask.Bounds bounds = PetHitMask.BuildFromSourcePixels(pixels, sourceWidth, sourceHeight);

			Assert.True(PetHitMask.IsPointOnModel(bounds, column + 0.5, row + 0.5, PetHitMask.Width, PetHitMask.Height));
		}
	}

	[Fact]
	public void SourcePixelsDoNotDilateIntoNeighboringCells()
	{
		const int sourceWidth = 960;
		const int sourceHeight = 1280;
		const int column = 10;
		const int row = 20;
		byte[] pixels = CreatePixels(sourceWidth, sourceHeight);
		SetAlphaAtCellSample(pixels, sourceWidth, sourceHeight, column, row, 0.5, 0.5);
		PetHitMask.Bounds bounds = PetHitMask.BuildFromSourcePixels(pixels, sourceWidth, sourceHeight);

		Assert.True(PetHitMask.IsPointOnModel(bounds, column + 0.5, row + 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, column - 0.5, row + 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, column + 1.5, row + 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, column + 0.5, row - 0.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, column + 0.5, row + 1.5, PetHitMask.Width, PetHitMask.Height));
	}

	[Fact]
	public void DistantVisiblePixelsProduceOneContinuousModelRectangle()
	{
		byte[] pixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtCellSample(pixels, PetHitMask.Width, PetHitMask.Height, 3, 2, 0.5, 0.5);
		SetAlphaAtCellSample(pixels, PetHitMask.Width, PetHitMask.Height, 7, 5, 0.5, 0.5);
		PetHitMask.Bounds bounds = PetHitMask.BuildFromSourcePixels(pixels, PetHitMask.Width, PetHitMask.Height);

		Assert.True(PetHitMask.IsPointOnModel(bounds, 5.5, 3.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, 2.5, 3.5, PetHitMask.Width, PetHitMask.Height));
		Assert.False(PetHitMask.IsPointOnModel(bounds, 8.5, 3.5, PetHitMask.Width, PetHitMask.Height));

		List<(int X, int Y, int Width, int Height)> regions = PetHitMask.BuildHitRegions(bounds, 192, 256);

		Assert.Single(regions);
		Assert.Equal((6, 4, 10, 8), regions[0]);
	}

	[Fact]
	public void HitRegionUsesFloorAndCeilingForFractionalClientSize()
	{
		PetHitMask.Bounds bounds = new(3, 2, 7, 5, HasBounds: true);

		List<(int X, int Y, int Width, int Height)> regions = PetHitMask.BuildHitRegions(bounds, 101.5, 203.25);

		Assert.Equal((3, 3, 6, 7), Assert.Single(regions));
	}

	[Fact]
	public void AlphaMustBeGreaterThanSixteen()
	{
		byte[] pixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(pixels, PetHitMask.Width, PetHitMask.Height, 12, 34, alpha: 16);
		PetHitMask.Bounds belowThreshold = PetHitMask.BuildFromSourcePixels(pixels, PetHitMask.Width, PetHitMask.Height);
		Assert.True(belowThreshold.IsEmpty);

		SetAlphaAtTop(pixels, PetHitMask.Width, PetHitMask.Height, 12, 34, alpha: 17);
		PetHitMask.Bounds aboveThreshold = PetHitMask.BuildFromSourcePixels(pixels, PetHitMask.Width, PetHitMask.Height);
		Assert.True(PetHitMask.IsPointOnModel(aboveThreshold, 12.5, 34.5, PetHitMask.Width, PetHitMask.Height));
	}

	[Fact]
	public void InvalidOrEmptyBuffersProduceNoHits()
	{
		byte[] validSizePixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		PetHitMask.Bounds noVisiblePixels = PetHitMask.BuildFromSourcePixels(validSizePixels, PetHitMask.Width, PetHitMask.Height);

		Assert.True(noVisiblePixels.IsEmpty);
		Assert.False(PetHitMask.IsPointOnModel(noVisiblePixels, 1, 1, 100, 100));
		Assert.False(PetHitMask.IsPointOnModel(default, 1, 1, 100, 100));
		Assert.Empty(PetHitMask.BuildHitRegions(noVisiblePixels, 100, 100));
		Assert.Empty(PetHitMask.BuildHitRegions(noVisiblePixels, 0, 100));
		Assert.True(PetHitMask.BuildFromSourcePixels([], 0, 0).IsEmpty);
		Assert.True(PetHitMask.BuildFromSourcePixels(new byte[3], 1, 1).IsEmpty);
		Assert.True(PetHitMask.BuildFromReducedPixels(new byte[3], 1, 1).IsEmpty);
	}

	[Fact]
	public void SourceSamplingDoesNotAllocatePerFrameMaskCopies()
	{
		byte[] pixels = CreatePixels(PetHitMask.Width, PetHitMask.Height);
		SetAlphaAtTop(pixels, PetHitMask.Width, PetHitMask.Height, 48, 64);
		_ = PetHitMask.BuildFromSourcePixels(pixels, PetHitMask.Width, PetHitMask.Height);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int index = 0; index < 8; index++)
		{
			_ = PetHitMask.BuildFromSourcePixels(pixels, PetHitMask.Width, PetHitMask.Height);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(0L, allocated);
	}

	private static byte[] CreatePixels(int width, int height) => new byte[checked(width * height * 4)];

	private static void SetAlphaAtTop(byte[] pixels, int width, int height, int x, int topY, byte alpha = byte.MaxValue)
	{
		int glY = height - 1 - topY;
		pixels[(glY * width + x) * 4 + 3] = alpha;
	}

	private static void SetAlphaAtCellSample(
		byte[] pixels,
		int width,
		int height,
		int column,
		int row,
		double xFraction,
		double yFraction,
		byte alpha = byte.MaxValue)
	{
		int x = Math.Min(width - 1, Math.Max(0, (int)Math.Floor((column + xFraction) * width / PetHitMask.Width)));
		int topY = Math.Min(height - 1, Math.Max(0, (int)Math.Floor((row + yFraction) * height / PetHitMask.Height)));
		SetAlphaAtTop(pixels, width, height, x, topY, alpha);
	}
}
