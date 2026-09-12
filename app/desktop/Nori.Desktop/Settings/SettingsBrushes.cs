using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nori.Desktop.Settings;

/// <summary>所有原生设置颜色都读取同一个主题资源，支持控件挂载前的初始化。</summary>
internal static class SettingsBrushes
{
	private static readonly Lazy<Styles> Theme = new(() =>
		(Styles)AvaloniaXamlLoader.Load(new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml")));

	public static IBrush Resolve(Control owner, string key)
	{
		if (owner.TryFindResource(key, owner.ActualThemeVariant, out object? value) && value is IBrush brush)
			return brush;
		if (Theme.Value.TryGetResource(key, owner.ActualThemeVariant, out value) && value is IBrush fallback)
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
