namespace Nori.Core.Live2D;

/// <summary>
/// 桌宠尺寸与布局计算（纯函数）
///
/// 将原 fit-model.ts 的安全基准计算下沉至 Core，并在宿主中统一窗口尺寸与投影矩阵映射。
/// </summary>
public static class PetSizing
{
	public const double DefaultPetWidth = 400;
	public const double DefaultPetHeight = 520;
	public const double MaxPetBaseWidth = 600;
	public const double MaxPetBaseHeight = 700;
	public const double MinPetBaseWidth = 240;
	public const double MinPetBaseHeight = 320;

	/// <summary>
	/// 计算安全基准视口尺寸 (DIP)
	///
	/// 防止模型原始画布（如 2048x2048 或 4096）过大导致窗口尺寸爆炸与显存浪费。
	/// </summary>
	public static (double Width, double Height) CalculateSafeBaseSize(double rawWidth, double rawHeight)
	{
		if (rawWidth <= 0 || rawHeight <= 0 || !double.IsFinite(rawWidth) || !double.IsFinite(rawHeight))
		{
			return (DefaultPetWidth, DefaultPetHeight);
		}

		if (rawWidth <= MaxPetBaseWidth &&
		    rawHeight <= MaxPetBaseHeight &&
		    rawWidth >= MinPetBaseWidth &&
		    rawHeight >= MinPetBaseHeight)
		{
			return (Math.Round(rawWidth), Math.Round(rawHeight));
		}

		double aspect = Math.Max(0.3, Math.Min(3.0, rawWidth / rawHeight));

		double fitW = Math.Round(DefaultPetHeight * aspect);
		double fitH = DefaultPetHeight;

		if (fitW > MaxPetBaseWidth)
		{
			fitW = MaxPetBaseWidth;
			fitH = Math.Round(MaxPetBaseWidth / aspect);
		}
		else if (fitW < MinPetBaseWidth)
		{
			fitW = MinPetBaseWidth;
			fitH = Math.Round(MinPetBaseWidth / aspect);
		}

		fitH = Math.Max(MinPetBaseHeight, Math.Min(MaxPetBaseHeight, fitH));

		return (fitW, fitH);
	}

	/// <summary>
	/// 根据模型原始宽高、用户缩放比例和屏幕尺寸，计算窗口最终物理像素尺寸
	///
	/// 窗口尺寸 = 安全基准 x userScale，并允许扩展到工作区 200%，让高倍缩放仍保留完整模型区域。
	/// </summary>
	public static (int Width, int Height) CalculateWindowSize(
		double rawWidth,
		double rawHeight,
		double userScale,
		double screenWidth,
		double screenHeight,
		double renderScaling)
	{
		double clampedScale = Math.Clamp(double.IsFinite(userScale) && userScale > 0 ? userScale : 1.0, 0.1, 4.0);
		var (baseW, baseH) = CalculateSafeBaseSize(rawWidth, rawHeight);

		double screenW = screenWidth > 0 ? screenWidth : 1920;
		double screenH = screenHeight > 0 ? screenHeight : 1080;
		double scaleFactor = renderScaling > 0 ? renderScaling : 1.0;

		int maxPhysicalW = Math.Max(200, (int)Math.Round(screenW * scaleFactor * 2.0));
		int maxPhysicalH = Math.Max(200, (int)Math.Round(screenH * scaleFactor * 2.0));

		int targetPhysicalW = Math.Max(80, (int)Math.Round(baseW * clampedScale * scaleFactor));
		int targetPhysicalH = Math.Max(80, (int)Math.Round(baseH * clampedScale * scaleFactor));

		int finalW = Math.Min(maxPhysicalW, targetPhysicalW);
		int finalH = Math.Min(maxPhysicalH, targetPhysicalH);

		return (finalW, finalH);
	}
}
