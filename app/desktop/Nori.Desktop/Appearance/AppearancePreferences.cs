using System.Globalization;
using Nori.Core.Configuration;

namespace Nori.Desktop.Appearance;

/// <summary>动效偏好档位。</summary>
public enum MotionMode
{
	/// <summary>按系统的「减少动画」设置。</summary>
	System,

	/// <summary>始终减少动效。</summary>
	Reduce,

	/// <summary>始终播放动效。</summary>
	Full,
}

/// <summary>
/// 个性化外观偏好：强调色、界面缩放、对话气泡与动效。
///
/// 这里的允许值同时供桥接校验与设置页选项使用。读取时遇到未知值回退默认 ——
/// 一条写坏的配置不该挡住启动，而写入侧由桥接严格校验。
/// </summary>
public sealed record AppearancePreferences(string Accent, int UiScale, string BubbleFont, string BubbleSize, MotionMode Motion)
{
	/// <summary>默认界面缩放百分比。</summary>
	internal const int DefaultUiScale = 100;

	/// <summary>圆润气泡字体 (Nunito)。</summary>
	internal const string BubbleFontRounded = "rounded";

	/// <summary>与界面相同的气泡字体 (Plus Jakarta Sans)。</summary>
	internal const string BubbleFontInterface = "interface";

	/// <summary>系统默认字体；中文字形也随之改变。</summary>
	internal const string BubbleFontSystem = "system";

	/// <summary>标准气泡字号。</summary>
	internal const string BubbleSizeStandard = "standard";

	/// <summary>大号气泡字号。</summary>
	internal const string BubbleSizeLarge = "large";

	/// <summary>可选的界面缩放百分比。</summary>
	internal static readonly IReadOnlyList<int> UiScales = [90, DefaultUiScale, 110, 125];

	/// <summary>可选的气泡字体。</summary>
	internal static readonly IReadOnlyList<string> BubbleFonts = [BubbleFontRounded, BubbleFontInterface, BubbleFontSystem];

	/// <summary>可选的气泡字号。</summary>
	internal static readonly IReadOnlyList<string> BubbleSizes = [BubbleSizeStandard, BubbleSizeLarge];

	/// <summary>动效偏好的配置文本，与 <see cref="MotionMode"/> 一一对应。</summary>
	internal static readonly IReadOnlyList<string> MotionModes = ["system", "reduce", "full"];

	/// <summary>未做任何选择时的外观，与引入个性化之前一致。</summary>
	internal static AppearancePreferences Default { get; } = new(
		NoriAccentPresets.Default, DefaultUiScale, BubbleFontRounded, BubbleSizeStandard, MotionMode.System);

	/// <summary>界面缩放倍率。</summary>
	internal double Scale => UiScale / 100d;

	/// <summary>从配置读取外观偏好；会访问数据库，不能在 UI 线程调用。</summary>
	internal static AppearancePreferences Read(ConfigStore config)
	{
		ArgumentNullException.ThrowIfNull(config);
		string accent = config.GetStringOr(ConfigStore.KeyUiAccent, Default.Accent);
		string scale = config.GetStringOr(ConfigStore.KeyUiScale, "");
		string font = config.GetStringOr(ConfigStore.KeyBubbleFont, Default.BubbleFont);
		string size = config.GetStringOr(ConfigStore.KeyBubbleSize, Default.BubbleSize);
		return new AppearancePreferences(
			NoriAccentPresets.Contains(accent) ? accent : Default.Accent,
			int.TryParse(scale, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) && UiScales.Contains(percent) ? percent : DefaultUiScale,
			BubbleFonts.Contains(font) ? font : Default.BubbleFont,
			BubbleSizes.Contains(size) ? size : Default.BubbleSize,
			TryParseMotion(config.GetStringOr(ConfigStore.KeyMotion, ""), out MotionMode motion) ? motion : Default.Motion);
	}

	/// <summary>动效档位的配置文本。</summary>
	internal static string FormatMotion(MotionMode mode) => MotionModes[(int)mode];

	/// <summary>解析动效配置文本；不认识的值返回 false。</summary>
	internal static bool TryParseMotion(string? value, out MotionMode mode)
	{
		for (int index = 0; index < MotionModes.Count; index++)
		{
			if (!string.Equals(MotionModes[index], value, StringComparison.Ordinal)) continue;
			mode = (MotionMode)index;
			return true;
		}
		mode = MotionMode.System;
		return false;
	}
}
