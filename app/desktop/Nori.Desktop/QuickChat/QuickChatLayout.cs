using Avalonia;

namespace Nori.Desktop.QuickChat;

/// <summary>屏幕物理坐标与浮窗 DIP 之间的统一布局；阴影和聚焦放大也留在工作区内。</summary>
internal readonly record struct QuickChatPlacement(PixelPoint Position, double Width, double Height);

internal static class QuickChatLayout
{
	internal const double ShadowMargin = 14;
	internal const double ComposerHeight = 46;
	internal static QuickChatPlacement Calculate(PixelPoint petPosition, Size petSize, double scaling,
		PixelRect workArea, double contentHeight)
	{
		double scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
		double width = Math.Min(280 + ShadowMargin * 2, workArea.Width / scale);
		double height = Math.Clamp(double.IsFinite(contentHeight) ? contentHeight : ComposerHeight + ShadowMargin * 2,
			Math.Min(ComposerHeight + ShadowMargin * 2, workArea.Height / scale), workArea.Height / scale);
		int pixelsW = (int)Math.Ceiling(width * scale), pixelsH = (int)Math.Ceiling(height * scale);
		int x = (int)Math.Round(petPosition.X + petSize.Width * scale / 2 - pixelsW / 2d);
		// 输入条上缘覆盖模型裁剪下沿 2 DIP，气泡增高只向上扩展。
		int bottom = (int)Math.Round(petPosition.Y + (petSize.Height - 2 + ComposerHeight + ShadowMargin) * scale);
		int y = bottom - pixelsH;
		return new QuickChatPlacement(new PixelPoint(
			Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - pixelsW)),
			Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - pixelsH))), width, height);
	}
}
