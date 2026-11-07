using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 对话气泡文字跟随个性化设置的字体与字号。
///
/// 绑定的是应用资源而不是取一次值：偏好变化时，已经显示的气泡就地换字体与字号，
/// 不必重建消息列表。
/// </summary>
internal static class BubbleTypography
{
	/// <summary>绑定字体、字号与行高；<paramref name="baseSize"/> 与 <paramref name="lineHeight"/> 是标准档取值。</summary>
	internal static T Apply<T>(T text, double baseSize, double lineHeight = double.NaN) where T : TextBlock
	{
		ArgumentNullException.ThrowIfNull(text);
		text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable(NoriAppearance.BubbleFontFamilyKey));
		text.Bind(TextBlock.FontSizeProperty, Scaled(text, baseSize));
		if (double.IsFinite(lineHeight)) text.Bind(TextBlock.LineHeightProperty, Scaled(text, lineHeight));
		return text;
	}

	/// <summary>绑定输入框、视图根等模板控件的字体与字号，未显式设置字体的子元素按继承取值。</summary>
	internal static T ApplyControl<T>(T control, double baseSize) where T : TemplatedControl
	{
		ArgumentNullException.ThrowIfNull(control);
		control.Bind(TemplatedControl.FontFamilyProperty, control.GetResourceObservable(NoriAppearance.BubbleFontFamilyKey));
		control.Bind(TemplatedControl.FontSizeProperty, Scaled(control, baseSize));
		return control;
	}

	private static IObservable<object?> Scaled(Control control, double value) =>
		control.GetResourceObservable(NoriAppearance.BubbleFontScaleKey, scale => value * (scale is double factor && double.IsFinite(factor) && factor > 0 ? factor : 1));
}
