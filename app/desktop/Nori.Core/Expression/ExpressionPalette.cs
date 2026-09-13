using System.Globalization;
using Nori.Core.Emotion;

namespace Nori.Core.Expression;

/// <summary>
/// 表达通道的侵入程度。
///
/// 分级的用途是默认值和变化速度：改整个系统强调色和让托盘图标换个颜色，打扰程度差着量级，
/// 不能用同一个开关也不能用同一个变化节奏。
/// </summary>
public enum Intrusiveness
{
	/// <summary>局部，只在她自己身上。默认开。</summary>
	Local,

	/// <summary>外围，在你视野边缘或另一个感官上。默认开。</summary>
	Peripheral,

	/// <summary>全局，改变你看的每一个窗口或听到的一切。**默认关**，且必须缓变。</summary>
	Global,
}

/// <summary>
/// 一组与具体通道无关的表达参数。
///
/// 通道只管「怎么把这组参数变成自己的形式」，不管「happy 是什么颜色」。映射只有一处，
/// 因此各通道的表现天然一致 —— 不会出现光晕是粉的而灯效是蓝的。加通道就是加一个订阅者。
/// </summary>
public sealed record ExpressionPalette
{
	/// <summary>
	/// 情绪类型。
	///
	/// 颜色表达不了全部差别：行为通道要按它决定挡不挡路，`sad` 和 `fond` 的颜色可以接近
	/// 但行为完全不同。带上它可以让通道不必再依赖 <c>EmotionState</c>。
	/// </summary>
	public required string Emotion { get; init; }

	/// <summary>主色，`#RRGGBB`。</summary>
	public required string Primary { get; init; }

	/// <summary>辅色，`#RRGGBB`。用于渐变的另一端、次要元素。</summary>
	public required string Accent { get; init; }

	/// <summary>强度 0–1。情绪越弱越接近中性，通道据此决定饱和度或音量。</summary>
	public required double Intensity { get; init; }

	/// <summary>节奏 0–1。呼吸、脉动、环境音的快慢。</summary>
	public required double Tempo { get; init; }

	/// <summary>音景标识，供环境音通道取素材。</summary>
	public required string Soundscape { get; init; }
}

/// <summary>
/// 情绪到表达参数的唯一映射。
///
/// 纯函数、无副作用，因此可以完整测试 —— 这一层错了所有通道一起错，它值得被钉死。
/// </summary>
public static class EmotionExpression
{
	/// <summary>中性时的主色。其余情绪在强度趋零时都收敛到它附近。</summary>
	public const string NeutralPrimary = "#8FA3B0";

	private static readonly Dictionary<string, (string Primary, string Accent, double Tempo, string Soundscape)> Map =
		new(StringComparer.Ordinal)
		{
			[EmotionTypes.Neutral] = (NeutralPrimary, "#B9C7D0", 0.35, "calm"),
			[EmotionTypes.Happy] = ("#F2B33D", "#FFD980", 0.75, "bright"),
			[EmotionTypes.Sad] = ("#5B7FA6", "#8FAAC6", 0.20, "quiet"),
			[EmotionTypes.Angry] = ("#C4462F", "#E2765E", 0.85, "tense"),
			[EmotionTypes.Surprised] = ("#4FC3D9", "#A6E7F2", 0.95, "alert"),
			[EmotionTypes.Shy] = ("#E39AB4", "#F5C8D8", 0.45, "soft"),
			[EmotionTypes.Sleepy] = ("#6C5B8C", "#9A8CB5", 0.12, "drowsy"),
			[EmotionTypes.Fond] = ("#E0708C", "#F2A8BC", 0.50, "warm"),
		};

	/// <summary>
	/// 取这个情绪的表达参数。
	///
	/// 强度趋零时主色向中性收敛：低强度的 happy 应该接近平静而不是一个淡黄色 —— 后者会让
	/// 「她有点开心」和「她很开心」在通道上只差饱和度，而实际差别应该是「几乎看不出来」。
	/// </summary>
	public static ExpressionPalette For(EmotionState? state)
	{
		string type = state?.Type ?? EmotionTypes.Neutral;
		double intensity = Math.Clamp(state?.Intensity ?? 0, 0, 1);
		if (!Map.TryGetValue(type, out var entry)) entry = Map[EmotionTypes.Neutral];

		(string neutralPrimary, string neutralAccent, double neutralTempo, _) = Map[EmotionTypes.Neutral];
		return new ExpressionPalette
		{
			Emotion = type,
			Primary = Blend(neutralPrimary, entry.Primary, intensity),
			Accent = Blend(neutralAccent, entry.Accent, intensity),
			Intensity = intensity,
			Tempo = neutralTempo + ((entry.Tempo - neutralTempo) * intensity),
			Soundscape = intensity < 0.15 ? Map[EmotionTypes.Neutral].Soundscape : entry.Soundscape,
		};
	}

	/// <summary>在两个 `#RRGGBB` 之间按比例线性混合。</summary>
	public static string Blend(string from, string to, double ratio)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(from);
		ArgumentException.ThrowIfNullOrWhiteSpace(to);
		double clamped = Math.Clamp(ratio, 0, 1);
		(int fromR, int fromG, int fromB) = Parse(from);
		(int toR, int toG, int toB) = Parse(to);

		return Format(
			Mix(fromR, toR, clamped),
			Mix(fromG, toG, clamped),
			Mix(fromB, toB, clamped));
	}

	private static int Mix(int from, int to, double ratio) => (int)Math.Round(from + ((to - from) * ratio));

	private static (int R, int G, int B) Parse(string hex)
	{
		string value = hex.TrimStart('#');
		if (value.Length != 6) throw new FormatException($"颜色必须是 #RRGGBB: {hex}");
		return (
			int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			int.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			int.Parse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
	}

	private static string Format(int r, int g, int b) =>
		"#" + (r << 16 | g << 8 | b).ToString("X6", CultureInfo.InvariantCulture);
}
