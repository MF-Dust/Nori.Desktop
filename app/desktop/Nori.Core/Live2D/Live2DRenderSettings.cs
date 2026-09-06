namespace Nori.Core.Live2D;

/// <summary>原生桌宠渲染质量模式。</summary>
public enum Live2DQualityMode
{
	Adaptive,
	Quality,
	Eco,
}

/// <summary>渲染质量策略使用的电源状态。</summary>
public enum Live2DPowerSource
{
	Ac,
	Battery,
}

/// <summary>
/// 原生 Live2D 显示配置的归一化结果。
///
/// 该类型是纯函数边界，避免 SQLite 的字符串推断和桥接 JSON 在不同入口产生不同的渲染行为。
/// </summary>
public sealed record Live2DRenderSettings
{
	public const float DefaultOpacity = 1.0f;
	public const float DefaultRenderScale = 2.0f;
	public const int DefaultMaxFps = 0;
	public const string DefaultQualityMode = "adaptive";
	public const float MinOpacity = 0.0f;
	public const float MaxOpacity = 1.0f;
	public const float MinRenderScale = 0.5f;
	public const float MaxRenderScale = 4.0f;
	public const int MaxExplicitFps = 240;

	public string ModelId { get; init; } = "arg-nori";
	public float Opacity { get; init; } = DefaultOpacity;
	public bool ShadowEnabled { get; init; } = true;
	public float RenderScale { get; init; } = DefaultRenderScale;
	public Live2DQualityMode QualityMode { get; init; } = Live2DQualityMode.Adaptive;
	public int MaxFps { get; init; } = DefaultMaxFps;

	/// <summary>
	/// 归一化渲染配置。
	///
	/// 未知模型仍保留原 ID 以兼容已有资源，但不会被误认为是本轮原生模型目录。
	/// </summary>
	public static Live2DRenderSettings Normalize(
		string? modelId,
		float? opacity = null,
		bool? shadowEnabled = null,
		float? renderScale = null,
		string? qualityMode = null,
		int? maxFps = null)
	{
		string normalizedModelId = string.IsNullOrWhiteSpace(modelId) ? "arg-nori" : modelId.Trim();
		return new Live2DRenderSettings
		{
			ModelId = normalizedModelId,
			Opacity = ClampFinite(opacity ?? DefaultOpacity, DefaultOpacity, MinOpacity, MaxOpacity),
			ShadowEnabled = shadowEnabled ?? true,
			RenderScale = ClampFinite(renderScale ?? DefaultRenderScale, DefaultRenderScale, MinRenderScale, MaxRenderScale),
			QualityMode = ParseQualityMode(qualityMode) ?? Live2DQualityMode.Adaptive,
			MaxFps = Math.Clamp(maxFps ?? DefaultMaxFps, 0, MaxExplicitFps),
		};
	}

	/// <summary>解析质量模式；未知值返回 null，由调用方决定默认值。</summary>
	public static Live2DQualityMode? ParseQualityMode(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"adaptive" => Live2DQualityMode.Adaptive,
		"quality" => Live2DQualityMode.Quality,
		"eco" => Live2DQualityMode.Eco,
		_ => null,
	};

	/// <summary>把质量模式写成配置存储值。</summary>
	public static string QualityModeToStorage(Live2DQualityMode mode) => mode switch
	{
		Live2DQualityMode.Quality => "quality",
		Live2DQualityMode.Eco => "eco",
		_ => "adaptive",
	};

	private static float ClampFinite(float value, float fallback, float min, float max) =>
		float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
