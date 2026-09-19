namespace Nori.Core.Live2D;

/// <summary>伴侣模型的展示模式。</summary>
public enum PetPresentationMode
{
	Ordinary,
	QuickChat,
}

/// <summary>模型画布内的归一化矩形，原点位于左上角。</summary>
public readonly record struct PetNormalizedRect(double X, double Y, double Width, double Height)
{
	public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
		&& double.IsFinite(Width) && double.IsFinite(Height)
		&& Width > 0 && Height > 0;
}

/// <summary>根据 Drawable 顶点得到的模型实际可见范围。</summary>
public readonly record struct PetDrawableGeometry(PetNormalizedRect Bounds)
{
	public static PetDrawableGeometry FullCanvas { get; } = new(new PetNormalizedRect(0, 0, 1, 1));

	public PetNormalizedRect SafeBounds => Bounds.IsValid
		? new PetNormalizedRect(
			Math.Clamp(Bounds.X, 0, 1),
			Math.Clamp(Bounds.Y, 0, 1),
			Math.Clamp(Bounds.Width, 0.001, 1 - Math.Clamp(Bounds.X, 0, 0.999)),
			Math.Clamp(Bounds.Height, 0.001, 1 - Math.Clamp(Bounds.Y, 0, 0.999)))
		: FullCanvas.Bounds;
}

/// <summary>Quick Chat 与伴侣视图之间共享的固定布局基准。</summary>
public readonly record struct PetQuickChatLayout(
	double ViewportWidth,
	double ViewportHeight,
	double ComposerWidth,
	double ComposerHeight,
	double VisualCenterX,
	double VisualTopPadding,
	double VisualBottom,
	double VisualMaxWidth)
{
	public static PetQuickChatLayout Default { get; } = new(
		ViewportWidth: 280,
		ViewportHeight: 190,
		ComposerWidth: 280,
		ComposerHeight: 46,
		VisualCenterX: 82,
		VisualTopPadding: 6,
		VisualBottom: 190,
		VisualMaxWidth: 180);

	/// <summary>输入框左上角相对伴侣窗口左上角的锚点。</summary>
	public (double X, double Y) ComposerAnchor => (0, ViewportHeight);
}

/// <summary>上半身裁切参数，所有比例都相对于 Drawable 实际范围。</summary>
public readonly record struct PetPresentationProfile(
	double CropWidthFraction,
	double CropHeightFraction,
	double CropCenterOffsetXFraction,
	double CropTopInsetFraction,
	PetQuickChatLayout Layout)
{
	/// <summary>将模型预设解析为最终归一化裁切矩形。</summary>
	public PetNormalizedRect ResolveCrop(PetDrawableGeometry geometry)
	{
		PetNormalizedRect bounds = geometry.SafeBounds;
		double width = Math.Clamp(bounds.Width * CropWidthFraction, 0.05, bounds.Width);
		double height = Math.Clamp(bounds.Height * CropHeightFraction, 0.05, bounds.Height);
		double centerX = bounds.X + bounds.Width * (0.5 + CropCenterOffsetXFraction);
		double left = Math.Clamp(centerX - width / 2, bounds.X, bounds.X + bounds.Width - width);
		double top = Math.Clamp(
			bounds.Y + bounds.Height * CropTopInsetFraction,
			bounds.Y,
			bounds.Y + bounds.Height - height);
		return new PetNormalizedRect(left, top, width, height);
	}
}

/// <summary>Live2D 展示预设及投影计算。</summary>
public static class PetPresentation
{
	public const double MinQuickChatScale = 0.75;
	public const double MaxQuickChatScale = 1.5;

	private static readonly PetPresentationProfile ArgNoriProfile = new(
		CropWidthFraction: 0.632,
		CropHeightFraction: 0.345,
		CropCenterOffsetXFraction: 0,
		CropTopInsetFraction: 0,
		PetQuickChatLayout.Default);

	private static readonly PetPresentationProfile NoriProfile = new(
		CropWidthFraction: 0.79,
		CropHeightFraction: 0.42,
		CropCenterOffsetXFraction: 0,
		CropTopInsetFraction: 0,
		PetQuickChatLayout.Default);

	private static readonly PetPresentationProfile ConservativeProfile = new(
		CropWidthFraction: 1,
		CropHeightFraction: 0.46,
		CropCenterOffsetXFraction: 0,
		CropTopInsetFraction: 0,
		PetQuickChatLayout.Default with { VisualCenterX = 140, VisualMaxWidth = 190 });

	/// <summary>获取模型预设；未知模型保守显示顶部上半身。</summary>
	public static PetPresentationProfile ForModel(string? modelId)
	{
		string normalized = modelId?.Trim().ToLowerInvariant() ?? "";
		return normalized switch
		{
			"arg-nori" or "default" => ArgNoriProfile,
			"nori" => NoriProfile,
			_ => ConservativeProfile,
		};
	}

	/// <summary>
	/// 按用户缩放扩展头像区域高度，输入框宽高和头像相对输入框的横向锚点保持不变。
	/// 极端桌宠缩放在 Quick Chat 中收口，避免遮住过多桌面。
	/// </summary>
	public static PetQuickChatLayout ScaleQuickChatLayout(PetQuickChatLayout layout, double userScale)
	{
		double scale = Math.Clamp(
			double.IsFinite(userScale) && userScale > 0 ? userScale : 1,
			MinQuickChatScale,
			MaxQuickChatScale);
		double contentHeight = (layout.VisualBottom - layout.VisualTopPadding) * scale;
		double viewportHeight = layout.VisualTopPadding + contentHeight;
		return layout with
		{
			ViewportHeight = viewportHeight,
			VisualBottom = viewportHeight,
			VisualMaxWidth = layout.VisualMaxWidth * scale,
		};
	}

	/// <summary>将画布坐标转换为当前裁切头像内的眼神追踪坐标。</summary>
	public static (double X, double Y) NormalizeQuickChatLookAt(
		double modelX,
		double modelY,
		PetDrawableGeometry geometry,
		PetPresentationProfile profile)
	{
		PetNormalizedRect crop = profile.ResolveCrop(geometry);
		double centerX = crop.X + crop.Width / 2;
		double centerY = crop.Y + crop.Height / 2;
		return (
			Math.Clamp((modelX - centerX) * 2 / crop.Width, -1, 1),
			Math.Clamp((centerY - modelY) * 2 / crop.Height, -1, 1));
	}

	/// <summary>
	/// 计算 Quick Chat 投影。裁切区域完整放入目标框并贴住输入框上沿，
	/// 因此耳朵不会被窗口顶部截断，底部只保留颈部与极少肩部。
	/// </summary>
	public static PetViewportProjection CalculateQuickChatProjection(
		double canvasWidth,
		double canvasHeight,
		double modelScaleX,
		double modelScaleY,
		double modelTranslateX,
		double modelTranslateY,
		PetDrawableGeometry geometry,
		PetPresentationProfile profile)
	{
		PetQuickChatLayout layout = profile.Layout;
		if (canvasWidth <= 0 || canvasHeight <= 0
			|| Math.Abs(modelScaleX) <= double.Epsilon || Math.Abs(modelScaleY) <= double.Epsilon
			|| layout.ViewportWidth <= 0 || layout.ViewportHeight <= 0)
		{
			return default;
		}

		PetNormalizedRect crop = profile.ResolveCrop(geometry);
		double cropCanvasWidth = crop.Width * canvasWidth;
		double cropCanvasHeight = crop.Height * canvasHeight;
		double cropModelWidth = Math.Abs(cropCanvasWidth * modelScaleX);
		double cropModelHeight = Math.Abs(cropCanvasHeight * modelScaleY);
		if (cropModelWidth <= double.Epsilon || cropModelHeight <= double.Epsilon) return default;

		double pixelsPerModelUnit = Math.Min(
			layout.VisualMaxWidth / cropModelWidth,
			(layout.VisualBottom - layout.VisualTopPadding) / cropModelHeight);
		double actualHeight = cropModelHeight * pixelsPerModelUnit;
		double targetCenterY = layout.VisualBottom - actualHeight / 2;

		double cropCanvasCenterX = (crop.X + crop.Width / 2 - 0.5) * canvasWidth;
		double cropCanvasCenterY = (0.5 - crop.Y - crop.Height / 2) * canvasHeight;
		double cropModelCenterX = cropCanvasCenterX * modelScaleX + modelTranslateX;
		double cropModelCenterY = cropCanvasCenterY * modelScaleY + modelTranslateY;

		double scaleX = pixelsPerModelUnit * 2 / layout.ViewportWidth;
		double scaleY = pixelsPerModelUnit * 2 / layout.ViewportHeight;
		double targetNdcX = layout.VisualCenterX / layout.ViewportWidth * 2 - 1;
		double targetNdcY = 1 - targetCenterY / layout.ViewportHeight * 2;
		return new PetViewportProjection(
			scaleX,
			scaleY,
			targetNdcX - scaleX * cropModelCenterX,
			targetNdcY - scaleY * cropModelCenterY);
	}
}
