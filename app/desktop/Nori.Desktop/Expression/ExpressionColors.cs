using System.Globalization;
using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Expression;

/// <summary>表达参数里的颜色串与 Avalonia 颜色之间的转换。</summary>
public static class ExpressionColors
{
	/// <summary>
	/// 解析 `#RRGGBB`，套上指定不透明度。
	///
	/// 解析失败返回中性灰而不是抛：一条通道因为颜色串格式问题就崩掉，会连累协调器去记一条
	/// 看不懂的错误，而用户看到的是「她的表情不动了」。
	/// </summary>
	public static Color Parse(string hex, byte alpha = 255)
	{
		if (string.IsNullOrWhiteSpace(hex)) return Fallback(alpha);

		string value = hex.TrimStart('#');
		return value.Length == 6
			&& byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
			&& byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
			&& byte.TryParse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)
			? Color.FromArgb(alpha, r, g, b)
			: Fallback(alpha);
	}

	// 中性兜底色取次级文字令牌，与界面其余灰阶一致。
	private static Color Fallback(byte alpha)
	{
		Color color = NoriThemeTokens.Color("text-faint");
		return Color.FromArgb(alpha, color.R, color.G, color.B);
	}

	/// <summary>转成 Windows 那边用的 `0x00BBGGRR`。注册表与 DWM 都按这个字节序存颜色。</summary>
	public static uint ToWindowsBgr(Color color) =>
		(uint)(color.B << 16 | color.G << 8 | color.R);
}
