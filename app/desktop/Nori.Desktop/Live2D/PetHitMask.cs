namespace Nori.Desktop.Live2D;

/// <summary>
/// 伴侣视窗 alpha 命中掩码的纯数据处理。
///
/// OpenGL 回读像素按底部到顶部排列；这里统一转换为客户端从顶部到底部的 96x128 位图。
/// 先用格内五点采样找出可见模型的外接边界，再填成一个连续矩形：交互范围始终覆盖模型主体，
/// 不会出现只有头顶能拖、下半身断开的碎片区域。
/// </summary>
internal static class PetHitMask
{
	public const int Width = 96;
	public const int Height = 128;
	public const int AlphaThreshold = 16;
	public const int ByteLength = (Width * Height + 7) / 8;

	private const double SampleInset = 0.2;
	private const double SampleCenter = 0.5;
	private const double SampleOuter = 0.8;

	/// <summary>从完整 RGBA 回读缓冲生成命中位图，缓冲行序为 OpenGL 的底部到顶部。</summary>
	public static void BuildFromSourcePixels(ReadOnlySpan<byte> pixels, int width, int height, Span<byte> bits)
	{
		bits.Clear();
		if (!HasBuffer(pixels, width, height) || bits.Length < ByteLength) return;

		for (int row = 0; row < Height; row++)
		{
			for (int column = 0; column < Width; column++)
			{
				if (!HasVisibleSample(pixels, width, height, column, row)) continue;
				Set(bits, column, row);
			}
		}
		FillBoundingRectangle(bits);
	}

	/// <summary>
	/// 从已经降到 96x128 的命中 FBO 回读缓冲生成命中位图。
	/// FBO 每个像素已经由 GPU 完成格内五点 alpha 最大值合成，因此这里只取对应格一个值。
	/// </summary>
	public static void BuildFromReducedPixels(ReadOnlySpan<byte> pixels, int width, int height, Span<byte> bits)
	{
		bits.Clear();
		if (!HasBuffer(pixels, width, height) || bits.Length < ByteLength) return;

		for (int row = 0; row < Height; row++)
		{
			for (int column = 0; column < Width; column++)
			{
				int sampleX = MapCellCoordinate(column, SampleCenter, Width, width);
				int sampleTopY = MapCellCoordinate(row, SampleCenter, Height, height);
				int sampleY = height - 1 - sampleTopY;
				int alphaOffset = (sampleY * width + sampleX) * 4 + 3;
				if (pixels[alphaOffset] > AlphaThreshold) Set(bits, column, row);
			}
		}
		FillBoundingRectangle(bits);
	}

	/// <summary>判断客户端 DIP 坐标对应的掩码格是否命中。</summary>
	public static bool IsPointOnModel(ReadOnlySpan<byte> bits, double clientX, double clientY, double clientWidth, double clientHeight)
	{
		if (bits.Length < ByteLength
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
		return IsSet(bits, column, row);
	}

	/// <summary>把模型外接边界转换为单个客户端逻辑像素矩形。</summary>
	public static List<(int X, int Y, int Width, int Height)> BuildHitRegions(
		ReadOnlySpan<byte> bits,
		double clientWidth,
		double clientHeight)
	{
		List<(int X, int Y, int Width, int Height)> regions = [];
		if (bits.Length < ByteLength
			|| !double.IsFinite(clientWidth) || !double.IsFinite(clientHeight)
			|| clientWidth <= 0 || clientHeight <= 0
			|| !TryGetBounds(bits, out int left, out int top, out int right, out int bottom))
		{
			return regions;
		}

		double cellWidth = clientWidth / Width;
		double cellHeight = clientHeight / Height;
		int x = (int)Math.Floor(left * cellWidth);
		int y = (int)Math.Floor(top * cellHeight);
		int regionRight = (int)Math.Ceiling((right + 1) * cellWidth);
		int regionBottom = (int)Math.Ceiling((bottom + 1) * cellHeight);
		regions.Add((x, y, Math.Max(1, regionRight - x), Math.Max(1, regionBottom - y)));
		return regions;
	}

	private static void FillBoundingRectangle(Span<byte> bits)
	{
		if (!TryGetBounds(bits, out int left, out int top, out int right, out int bottom)) return;

		bits.Clear();
		for (int row = top; row <= bottom; row++)
		{
			for (int column = left; column <= right; column++) Set(bits, column, row);
		}
	}

	private static bool TryGetBounds(
		ReadOnlySpan<byte> bits,
		out int left,
		out int top,
		out int right,
		out int bottom)
	{
		left = Width;
		top = Height;
		right = -1;
		bottom = -1;
		if (bits.Length < ByteLength) return false;

		for (int row = 0; row < Height; row++)
		{
			for (int column = 0; column < Width; column++)
			{
				if (!IsSet(bits, column, row)) continue;
				left = Math.Min(left, column);
				top = Math.Min(top, row);
				right = Math.Max(right, column);
				bottom = Math.Max(bottom, row);
			}
		}
		return right >= left && bottom >= top;
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

	private static bool IsSet(ReadOnlySpan<byte> bits, int column, int row)
	{
		int index = row * Width + column;
		return (bits[index >> 3] & (1 << (index & 7))) != 0;
	}

	private static void Set(Span<byte> bits, int column, int row)
	{
		int index = row * Width + column;
		bits[index >> 3] |= (byte)(1 << (index & 7));
	}
}
