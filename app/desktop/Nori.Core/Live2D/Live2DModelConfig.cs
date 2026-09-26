using System.Globalization;
using Nori.Core.Configuration;

namespace Nori.Core.Live2D;

/// <summary>
/// Live2D 每模型配置与全局键回退。
///
/// 读取结果先经 <see cref="ConfigValue.AsStringOr"/> 还原。配置库存进去的 "1" / "0" / "true" / "false"
/// 会在读出时重新推断成布尔，再还原成 "true" / "false"；浮点解析因此也要接受这两个词。
/// 空字符串视为未设置。非空但无法解析的布尔回退全局键，与桌宠渲染一致。
/// </summary>
public static class Live2DModelConfig
{
	public const string ScaleKey = "l2d_scale";
	public const string OpacityKey = "l2d_opacity";
	public const string ShadowKey = "l2d_shadow";
	public const string RenderScaleKey = "l2d_render_scale";
	public const string QualityModeKey = "l2d_quality_mode";
	public const string MaxFpsKey = "l2d_max_fps";

	private static readonly string[] DisplayBases =
	[
		ScaleKey,
		OpacityKey,
		ShadowKey,
		RenderScaleKey,
		QualityModeKey,
		MaxFpsKey,
	];

	/// <summary>只存在全局键的行为项。</summary>
	public static IReadOnlyList<string> BehaviorKeys { get; } =
	[
		"l2d_auto_blink",
		"l2d_eye_tracking",
		"l2d_idle_eye_animation",
		"l2d_idle_animation",
		"l2d_expression_enabled",
		"l2d_lip_sync",
		"l2d_beat_sync",
		"l2d_click_interaction",
		"l2d_click_through",
	];

	/// <summary>每模型键，形如 <c>l2d_scale_arg-nori</c>。</summary>
	public static string ModelKey(string baseKey, string modelId) => $"{baseKey}_{modelId}";

	/// <summary>显示配置需要一次性读出的每模型键与全局键，按基键成对排列。</summary>
	public static string[] DisplayKeys(string modelId)
	{
		string[] keys = new string[DisplayBases.Length * 2];
		for (int index = 0; index < DisplayBases.Length; index++)
		{
			keys[index * 2] = ModelKey(DisplayBases[index], modelId);
			keys[index * 2 + 1] = DisplayBases[index];
		}
		return keys;
	}

	/// <summary>按 <see cref="ConfigValue.AsStringOr"/> 还原批量读取结果。缺失或无法表示成文本时返回空串。</summary>
	public static string ReadText(IReadOnlyDictionary<string, ConfigValue> values, string key) =>
		values.TryGetValue(key, out ConfigValue? value) ? ConfigValue.AsStringOr(value, "") : "";

	/// <summary>每模型文本非空则采用，否则全局文本非空则采用，否则 fallback。</summary>
	public static string ReadPreferredText(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, string fallback)
	{
		string modelValue = ReadText(values, ModelKey(baseKey, modelId));
		if (modelValue.Length > 0) return modelValue;
		string globalValue = ReadText(values, baseKey);
		return globalValue.Length > 0 ? globalValue : fallback;
	}

	/// <summary>解析数值文本。空白或无法解析时返回 null；<c>true</c> / <c>false</c> 为 1 / 0。</summary>
	public static float? ParseFloat(string raw)
	{
		if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1.0f;
		if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0.0f;
		return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;
	}

	/// <summary>解析布尔文本。<c>1</c> / <c>0</c> 与 <c>true</c> / <c>false</c> 不区分大小写，其余返回 null。</summary>
	public static bool? ParseBool(string raw) => raw switch
	{
		"1" => true,
		"0" => false,
		_ when raw.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
		_ when raw.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
		_ => null,
	};

	/// <summary>每模型浮点无法解析或为空白时回退全局键，再回退 fallback。</summary>
	public static float ReadFloat(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, float fallback) =>
		ParseFloat(ReadText(values, ModelKey(baseKey, modelId)))
		?? ParseFloat(ReadText(values, baseKey))
		?? fallback;

	/// <summary>每模型布尔。空白或无法解析时回退全局键，再回退 fallback。</summary>
	public static bool ReadBool(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, bool fallback)
	{
		string modelText = ReadText(values, ModelKey(baseKey, modelId));
		if (modelText.Length > 0 && ParseBool(modelText) is bool parsed) return parsed;
		return ParseBool(ReadText(values, baseKey)) ?? fallback;
	}
}
