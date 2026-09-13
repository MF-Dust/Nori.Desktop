namespace Nori.Core.Live2D;

/// <summary>模型画布在窗口中的可视矩形。</summary>
public readonly record struct PetViewportRect(double Left, double Top, double Width, double Height);

/// <summary>窗口 DIP 与模型画布归一化坐标之间的纯函数映射。</summary>
public readonly record struct PetViewportMapping
{
	private const double CoordinateEpsilon = 1e-9;

	public double ViewportWidth { get; }
	public double ViewportHeight { get; }
	public double CanvasWidth { get; }
	public double CanvasHeight { get; }
	public double FinalScaleX { get; }
	public double FinalScaleY { get; }
	public double FinalTranslateX { get; }
	public double FinalTranslateY { get; }

	private PetViewportMapping(
		double viewportWidth,
		double viewportHeight,
		double canvasWidth,
		double canvasHeight,
		double finalScaleX,
		double finalScaleY,
		double finalTranslateX,
		double finalTranslateY)
	{
		ViewportWidth = viewportWidth;
		ViewportHeight = viewportHeight;
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
		FinalScaleX = finalScaleX;
		FinalScaleY = finalScaleY;
		FinalTranslateX = finalTranslateX;
		FinalTranslateY = finalTranslateY;
	}

	/// <summary>
	/// 根据 PetRuntime.RenderFrame 的投影公式创建映射。
	/// modelScale/modelTranslate 是 CubismModelMatrix 当前值，调用方应在 GL 线程读取。
	/// </summary>
	public static PetViewportMapping Create(
		double viewportWidth,
		double viewportHeight,
		double canvasWidth,
		double canvasHeight,
		double modelScaleX,
		double modelScaleY,
		double modelTranslateX = 0,
		double modelTranslateY = 0)
	{
		if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
			|| !double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight)
			|| viewportWidth <= 0 || viewportHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
		{
			return default;
		}

		double aspectWindow = viewportWidth / viewportHeight;
		double aspectModel = canvasWidth / canvasHeight;
		double projectionScaleX = viewportHeight / viewportWidth;
		double projectionScaleY = 1.0;
		if (aspectModel > aspectWindow)
		{
			double fit = aspectWindow / aspectModel;
			projectionScaleX *= fit;
			projectionScaleY *= fit;
		}

		return new PetViewportMapping(
			viewportWidth,
			viewportHeight,
			canvasWidth,
			canvasHeight,
			projectionScaleX * modelScaleX,
			projectionScaleY * modelScaleY,
			projectionScaleX * modelTranslateX,
			projectionScaleY * modelTranslateY);
	}

	/// <summary>从已计算的最终变换创建映射，便于纯测试和特殊布局复用。</summary>
	public static PetViewportMapping FromFinalTransform(
		double viewportWidth,
		double viewportHeight,
		double canvasWidth,
		double canvasHeight,
		double finalScaleX,
		double finalScaleY,
		double finalTranslateX = 0,
		double finalTranslateY = 0) =>
		new(viewportWidth, viewportHeight, canvasWidth, canvasHeight,
			finalScaleX, finalScaleY, finalTranslateX, finalTranslateY);

	/// <summary>获取完整模型画布映射到窗口后的矩形。</summary>
	public PetViewportRect ModelRect
	{
		get
		{
			if (!IsValid) return default;
			(double left, double top) = ToClient(-CanvasWidth / 2, CanvasHeight / 2);
			(double right, double bottom) = ToClient(CanvasWidth / 2, -CanvasHeight / 2);
			return new PetViewportRect(left, top, right - left, bottom - top);
		}
	}

	public bool IsValid => ViewportWidth > 0 && ViewportHeight > 0
		&& CanvasWidth > 0 && CanvasHeight > 0
		&& double.IsFinite(FinalScaleX) && double.IsFinite(FinalScaleY)
		&& Math.Abs(FinalScaleX) > CoordinateEpsilon && Math.Abs(FinalScaleY) > CoordinateEpsilon;

	/// <summary>把窗口 DIP 坐标转换为模型画布归一化坐标。</summary>
	public bool TryMapClientToModel(double clientX, double clientY, out double modelX, out double modelY)
	{
		modelX = 0;
		modelY = 0;
		if (!IsValid || !double.IsFinite(clientX) || !double.IsFinite(clientY)
			|| clientX < 0 || clientX > ViewportWidth || clientY < 0 || clientY > ViewportHeight)
		{
			return false;
		}

		double ndcX = clientX / ViewportWidth * 2.0 - 1.0;
		double ndcY = -((clientY / ViewportHeight * 2.0) - 1.0);
		double canvasX = (ndcX - FinalTranslateX) / FinalScaleX;
		double canvasY = (ndcY - FinalTranslateY) / FinalScaleY;
		modelX = (canvasX + CanvasWidth / 2.0) / CanvasWidth;
		modelY = (CanvasHeight / 2.0 - canvasY) / CanvasHeight;
		if (modelX < -CoordinateEpsilon || modelX > 1 + CoordinateEpsilon
			|| modelY < -CoordinateEpsilon || modelY > 1 + CoordinateEpsilon)
		{
			modelX = 0;
			modelY = 0;
			return false;
		}

		modelX = Math.Clamp(modelX, 0, 1);
		modelY = Math.Clamp(modelY, 0, 1);
		return true;
	}

	/// <summary>把模型画布内的归一化矩形转换为窗口 DIP 矩形。</summary>
	public bool TryMapNormalizedRectToClient(
		double x,
		double y,
		double width,
		double height,
		out PetViewportRect clientRect)
	{
		clientRect = default;
		if (!IsValid || !double.IsFinite(x) || !double.IsFinite(y)
			|| !double.IsFinite(width) || !double.IsFinite(height)
			|| width < 0 || height < 0
			|| x < -CoordinateEpsilon || y < -CoordinateEpsilon
			|| x + width > 1 + CoordinateEpsilon || y + height > 1 + CoordinateEpsilon)
		{
			return false;
		}

		double leftCanvas = (Math.Clamp(x, 0, 1) - 0.5) * CanvasWidth;
		double rightCanvas = (Math.Clamp(x + width, 0, 1) - 0.5) * CanvasWidth;
		double topCanvas = (0.5 - Math.Clamp(y, 0, 1)) * CanvasHeight;
		double bottomCanvas = (0.5 - Math.Clamp(y + height, 0, 1)) * CanvasHeight;
		(double firstX, double firstY) = ToClient(leftCanvas, topCanvas);
		(double secondX, double secondY) = ToClient(rightCanvas, bottomCanvas);
		clientRect = new PetViewportRect(
			Math.Min(firstX, secondX),
			Math.Min(firstY, secondY),
			Math.Abs(secondX - firstX),
			Math.Abs(secondY - firstY));
		return true;
	}

	private (double X, double Y) ToClient(double canvasX, double canvasY)
	{
		double ndcX = FinalScaleX * canvasX + FinalTranslateX;
		double ndcY = FinalScaleY * canvasY + FinalTranslateY;
		return ((ndcX + 1.0) * ViewportWidth / 2.0, (1.0 - ndcY) * ViewportHeight / 2.0);
	}
}
