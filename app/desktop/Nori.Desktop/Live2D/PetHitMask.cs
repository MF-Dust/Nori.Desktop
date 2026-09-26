namespace Nori.Desktop.Live2D;

/// <summary>
/// 伴侣视窗 alpha 命中掩码的纯数据处理。
///
/// OpenGL 回读像素按底部到顶部排列；这里统一映射为客户端从顶部到底部的 96x128 网格。
/// 每格五点采样只累计一次外接矩形。交互范围始终覆盖模型主体，不会出现碎片区域。
/// </summary>
internal static class PetHitMask
{
	public const int Width = 96;
	public const int Height = 128;
	public const int AlphaThreshold = 16;

	private const double SampleInset = 0.2;
	private const double SampleCenter = 0.5;
	private const double SampleOuter = 0.8;

	/// <summary>网格外接矩形；默认值显式表示没有可命中区域。</summary>
	public readonly record struct Bounds(int Left, int Top, int Right, int Bottom, bool HasBounds)
	{
		public static Bounds Empty => default;
		public bool IsEmpty => !HasBounds;
	}

	/// <summary>从完整 RGBA 回读缓冲累计可见网格外接矩形，缓冲行序为 OpenGL 的底部到顶部。</summary>
	public static Bounds BuildFromSourcePixels(ReadOnlySpan<byte> pixels, int width, int height)
	{
		if (!HasBuffer(pixels, width, height)) return Bounds.Empty;

		int left = Width;
		int top = Height;
		int right = -1;
		int bottom = -1;
		for (int row = 0; row < Height; row++)
		{
			for (int column = 0; column < Width; column++)
			{
				if (!HasVisibleSample(pixels, width, height, column, row)) continue;
				left = Math.Min(left, column);
				top = Math.Min(top, row);
				right = Math.Max(right, column);
				bottom = Math.Max(bottom, row);
			}
		}

		return right >= left && bottom >= top
			? new Bounds(left, top, right, bottom, HasBounds: true)
			: Bounds.Empty;
	}

	/// <summary>
	/// 从已经降到 96x128 的命中 FBO 累计可见网格外接矩形。
	/// FBO 每个像素已经由 GPU 完成格内五点 alpha 最大值合成，因此这里只取对应格一个值。
	/// </summary>
	public static Bounds BuildFromReducedPixels(ReadOnlySpan<byte> pixels, int width, int height)
	{
		if (!HasBuffer(pixels, width, height)) return Bounds.Empty;

		int left = Width;
		int top = Height;
		int right = -1;
		int bottom = -1;
		for (int row = 0; row < Height; row++)
		{
			for (int column = 0; column < Width; column++)
			{
				int sampleX = MapCellCoordinate(column, SampleCenter, Width, width);
				int sampleTopY = MapCellCoordinate(row, SampleCenter, Height, height);
				int sampleY = height - 1 - sampleTopY;
				int alphaOffset = (sampleY * width + sampleX) * 4 + 3;
				if (pixels[alphaOffset] <= AlphaThreshold) continue;
				left = Math.Min(left, column);
				top = Math.Min(top, row);
				right = Math.Max(right, column);
				bottom = Math.Max(bottom, row);
			}
		}

		return right >= left && bottom >= top
			? new Bounds(left, top, right, bottom, HasBounds: true)
			: Bounds.Empty;
	}

	/// <summary>判断客户端 DIP 坐标对应的掩码格是否命中。</summary>
	public static bool IsPointOnModel(Bounds bounds, double clientX, double clientY, double clientWidth, double clientHeight)
	{
		if (bounds.IsEmpty
			|| !double.IsFinite(clientX) || !double.IsFinite(clientY)
			|| !double.IsFinite(clientWidth) || !double.IsFinite(clientHeight)
			|| clientWidth <= 0 || clientHeight <= 0
			|| clientX < 0 || clientX >= clientWidth
			|| clientY < 0 || clientY >= clientHeight)
		{
			return false;
		}

		int column = Math.Min(Width - 1, (int)(clientX / clientWidth * Width));
		int row = Math.Min(Height - 1, (int)(clientY / clientHeight * Height));
		return column >= bounds.Left && column <= bounds.Right
			&& row >= bounds.Top && row <= bounds.Bottom;
	}

	/// <summary>把网格外接边界转换为单个客户端逻辑像素矩形。</summary>
	public static List<(int X, int Y, int Width, int Height)> BuildHitRegions(
		Bounds bounds,
		double clientWidth,
		double clientHeight)
	{
		List<(int X, int Y, int Width, int Height)> regions = [];
		if (bounds.IsEmpty
			|| !double.IsFinite(clientWidth) || !double.IsFinite(clientHeight)
			|| clientWidth <= 0 || clientHeight <= 0)
		{
			return regions;
		}

		double cellWidth = clientWidth / Width;
		double cellHeight = clientHeight / Height;
		int x = (int)Math.Floor(bounds.Left * cellWidth);
		int y = (int)Math.Floor(bounds.Top * cellHeight);
		int regionRight = (int)Math.Ceiling((bounds.Right + 1) * cellWidth);
		int regionBottom = (int)Math.Ceiling((bounds.Bottom + 1) * cellHeight);
		regions.Add((x, y, Math.Max(1, regionRight - x), Math.Max(1, regionBottom - y)));
		return regions;
	}

	private static bool HasVisibleSample(ReadOnlySpan<byte> pixels, int width, int height, int column, int row)
	{
		return HasAlpha(pixels, width, height, column, row, SampleCenter, SampleCenter)
			|| HasAlpha(pixels, width, height, column, row, SampleInset, SampleInset)
			|| HasAlpha(pixels, width, height, column, row, SampleOuter, SampleInset)
			|| HasAlpha(pixels, width, height, column, row, SampleInset, SampleOuter)
			|| HasAlpha(pixels, width, height, column, row, SampleOuter, SampleOuter);
	}

	private static bool HasAlpha(
		ReadOnlySpan<byte> pixels,
		int width,
		int height,
		int column,
		int row,
		double xFraction,
		double yFraction)
	{
		int sampleX = MapCellCoordinate(column, xFraction, Width, width);
		int sampleTopY = MapCellCoordinate(row, yFraction, Height, height);
		int sampleY = height - 1 - sampleTopY;
		int alphaOffset = (sampleY * width + sampleX) * 4 + 3;
		return pixels[alphaOffset] > AlphaThreshold;
	}

	private static int MapCellCoordinate(int cell, double fraction, int cellCount, int pixelCount)
	{
		return Math.Min(pixelCount - 1, Math.Max(0, (int)Math.Floor((cell + fraction) * pixelCount / cellCount)));
	}

	private static bool HasBuffer(ReadOnlySpan<byte> pixels, int width, int height)
	{
		if (width <= 0 || height <= 0) return false;
		long requiredLength = (long)width * height * 4;
		return requiredLength <= pixels.Length;
	}
}
