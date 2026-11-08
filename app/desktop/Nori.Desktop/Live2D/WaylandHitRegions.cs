using Avalonia;

namespace Nori.Desktop.Live2D;

/// <summary>
/// Wayland 输入区用表面局部逻辑像素。先把模型网格映射到控件在窗口内的位置，再向外取整。
/// </summary>
internal static class WaylandHitRegions
{
	internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

	/// <summary>穿透开启时去掉模型矩形，仍保留快捷聊天等额外交互区。</summary>
	internal static PixelRect[] Compose(bool clickThrough, PetHitMask.Bounds mask, Rect model, IReadOnlyList<Rect> interactive)
	{
		List<PixelRect> regions = [];
		if (!clickThrough && TryModelRect(mask, model, out PixelRect modelRect)) regions.Add(modelRect);
		foreach (Rect rect in interactive)
		{
			PixelRect pixel = Outset(rect);
			if (pixel.Width > 0 && pixel.Height > 0) regions.Add(pixel);
		}
		return regions.ToArray();
	}

	/// <summary>表面尺寸与逻辑尺寸不一致时按比例缩放，四边仍然向外取整。</summary>
	internal static PixelRect[] FitSurface(PixelRect[] regions, int logicalWidth, int logicalHeight, int surfaceWidth, int surfaceHeight)
	{
		if (regions.Length == 0 || logicalWidth <= 0 || logicalHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0
			|| (logicalWidth == surfaceWidth && logicalHeight == surfaceHeight))
			return regions;
		double scaleX = (double)surfaceWidth / logicalWidth;
		double scaleY = (double)surfaceHeight / logicalHeight;
		PixelRect[] scaled = new PixelRect[regions.Length];
		for (int index = 0; index < regions.Length; index++)
		{
			PixelRect rect = regions[index];
			int left = (int)Math.Floor(rect.X * scaleX);
			int top = (int)Math.Floor(rect.Y * scaleY);
			int right = (int)Math.Ceiling((rect.X + rect.Width) * scaleX);
			int bottom = (int)Math.Ceiling((rect.Y + rect.Height) * scaleY);
			scaled[index] = new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
		}
		return scaled;
	}

	internal static bool Same(PixelRect[]? previous, PixelRect[] next)
	{
		if (previous is null || previous.Length != next.Length) return false;
		for (int index = 0; index < next.Length; index++)
			if (previous[index] != next[index]) return false;
		return true;
	}

	private static bool TryModelRect(PetHitMask.Bounds mask, Rect model, out PixelRect rect)
	{
		rect = default;
		if (mask.IsEmpty || model.Width <= 0 || model.Height <= 0 || !double.IsFinite(model.X) || !double.IsFinite(model.Y))
			return false;
		double cellWidth = model.Width / PetHitMask.Width;
		double cellHeight = model.Height / PetHitMask.Height;
		double left = model.X + mask.Left * cellWidth;
		double top = model.Y + mask.Top * cellHeight;
		double right = model.X + (mask.Right + 1) * cellWidth;
		double bottom = model.Y + (mask.Bottom + 1) * cellHeight;
		rect = Outset(new Rect(left, top, right - left, bottom - top));
		return rect.Width > 0 && rect.Height > 0;
	}

	private static PixelRect Outset(Rect rect)
	{
		if (rect.Width <= 0 || rect.Height <= 0 || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)) return default;
		int left = (int)Math.Floor(rect.X);
		int top = (int)Math.Floor(rect.Y);
		int right = (int)Math.Ceiling(rect.Right);
		int bottom = (int)Math.Ceiling(rect.Bottom);
		return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
	}
}
