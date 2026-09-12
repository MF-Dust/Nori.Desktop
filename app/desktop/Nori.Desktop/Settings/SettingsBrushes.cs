using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nori.Desktop.Settings;

/// <summary>所有原生设置颜色都读取同一个主题资源，支持控件挂载前的初始化。</summary>
internal static class SettingsBrushes
{
	private static readonly ConditionalWeakTable<Control, Styles> Themes = new();

	public static IBrush Resolve(Control owner, string key)
	{
		ThemeVariant theme = owner.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
		if (owner.TryFindResource(key, theme, out object? value) && value is IBrush brush)
			return brush;
		// 挂载前按控件缓存资源，避免多个 Headless UI 会话共用线程归属不同的画刷。
		Styles resources = Themes.GetValue(owner, _ =>
			(Styles)AvaloniaXamlLoader.Load(new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml")));
		if (resources.TryGetResource(key, theme, out value) && value is IBrush fallback)
			return fallback;
		throw new InvalidOperationException("缺少原生设置主题资源：" + key);
	}
}

/// <summary>为一个呈现器复用画刷，主题变化时就地换色而不销毁控件。</summary>
internal sealed class SettingsBrushPalette
{
	private readonly Control _owner;
	private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

	public SettingsBrushPalette(Control owner)
	{
		_owner = owner;
		owner.ActualThemeVariantChanged += (_, _) => Refresh();
	}

	public IBrush this[string key]
	{
		get
		{
			if (_brushes.TryGetValue(key, out SolidColorBrush? brush)) return brush;
			brush = new SolidColorBrush(((ISolidColorBrush)SettingsBrushes.Resolve(_owner, key)).Color);
			_brushes.Add(key, brush);
			return brush;
		}
	}

	private void Refresh()
	{
		foreach ((string key, SolidColorBrush brush) in _brushes)
			brush.Color = ((ISolidColorBrush)SettingsBrushes.Resolve(_owner, key)).Color;
	}
}
