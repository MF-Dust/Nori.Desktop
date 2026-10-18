using Avalonia;

namespace Nori.Desktop.QuickChat;

/// <summary>屏幕物理坐标与浮窗 DIP 之间的统一布局；阴影和聚焦放大也留在工作区内。</summary>
internal readonly record struct QuickChatPlacement(PixelPoint Position, double Width, double Height);

internal static class QuickChatLayout
{
	internal const double ShadowMargin = 14;
	internal const double ComposerHeight = 46;
	internal const double PetGap = 8;
	internal static QuickChatPlacement Calculate(PixelPoint petPosition, Size petSize, double scaling,
		PixelRect workArea, double contentHeight)
	{
		double scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
		double width = Math.Min(280 + ShadowMargin * 2, workArea.Width / scale);
		double height = Math.Clamp(double.IsFinite(contentHeight) ? contentHeight : ComposerHeight + ShadowMargin * 2,
			Math.Min(ComposerHeight + ShadowMargin * 2, workArea.Height / scale), workArea.Height / scale);
		int pixelsW = (int)Math.Ceiling(width * scale), pixelsH = (int)Math.Ceiling(height * scale);
		int x = (int)Math.Round(petPosition.X + petSize.Width * scale / 2 - pixelsW / 2d);
		// 内容从模型下沿往下排；窗口顶部的透明阴影边距不占可见间隔。
		int y = (int)Math.Round(petPosition.Y + (petSize.Height + PetGap - ShadowMargin) * scale);
		return new QuickChatPlacement(new PixelPoint(
			Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - pixelsW)),
			Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - pixelsH))), width, height);
	}
}
