using System.Globalization;
using Nori.Core.Configuration;

namespace Nori.Core.Live2D;

/// <summary>
/// Live2D 每模型显示配置与全局行为键读取。
///
/// 配置库存进去的 "1" / "0" / "true" / "false" 会在读出时重新推断成布尔，
/// 经 <see cref="ConfigValue.AsStringOr"/> 还原后浮点解析需同时接受 true/false。
/// </summary>
public static class Live2DModelConfig
{
	public const string ScaleKey = "l2d_scale";
	public const string OpacityKey = "l2d_opacity";
	public const string ShadowKey = "l2d_shadow";
	public const string RenderScaleKey = "l2d_render_scale";
	public const string QualityModeKey = "l2d_quality_mode";
	public const string MaxFpsKey = "l2d_max_fps";
	public const string AutonomousBehaviorKey = "l2d_autonomous_behavior";
	public const string AutonomousRandomKey = "l2d_autonomous_random";
	public const string AutonomousFrequencyKey = "l2d_autonomous_frequency";

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
		AutonomousBehaviorKey,
		AutonomousRandomKey,
		AutonomousFrequencyKey,
	];

	/// <summary>每模型键，形如 <c>l2d_scale_arg-nori</c>。</summary>
	public static string ModelKey(string baseKey, string modelId) => $"{baseKey}_{modelId}";

	/// <summary>显示配置需要一次性读出的每模型键列表。</summary>
	public static string[] DisplayKeys(string modelId)
	{
		string[] keys = new string[DisplayBases.Length];
		for (int index = 0; index < DisplayBases.Length; index++)
		{
			keys[index] = ModelKey(DisplayBases[index], modelId);
		}
		return keys;
	}

	/// <summary>按 <see cref="ConfigValue.AsStringOr"/> 还原批量读取结果。缺失或无法表示成文本时返回空串。</summary>
	public static string ReadText(IReadOnlyDictionary<string, ConfigValue> values, string key) =>
		values.TryGetValue(key, out ConfigValue? value) ? ConfigValue.AsStringOr(value, "") : "";

	/// <summary>每模型文本非空则采用，否则回退 fallback。</summary>
	public static string ReadPreferredText(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, string fallback)
	{
		string modelValue = ReadText(values, ModelKey(baseKey, modelId));
		return modelValue.Length > 0 ? modelValue : fallback;
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

	/// <summary>读取每模型浮点，无法解析或空白时回退 fallback。</summary>
	public static float ReadFloat(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, float fallback) =>
		ParseFloat(ReadText(values, ModelKey(baseKey, modelId))) ?? fallback;

	/// <summary>读取每模型布尔，无法解析或空白时回退 fallback。</summary>
	public static bool ReadBool(IReadOnlyDictionary<string, ConfigValue> values, string baseKey, string modelId, bool fallback) =>
		ParseBool(ReadText(values, ModelKey(baseKey, modelId))) ?? fallback;
}
