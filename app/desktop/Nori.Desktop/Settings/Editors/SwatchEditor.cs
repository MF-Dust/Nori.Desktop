using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Settings;

/// <summary>单选色块。选中项在色点外描一圈，并改用主文字色显示名称。</summary>
internal sealed class SwatchEditor : WrapPanel, ISettingsFieldEditor
{
	private const double DotSize = NoriMetrics.Spacing7;
	private const double RingThickness = NoriMetrics.Spacing0_5;

	private readonly SettingsFieldViewModel _field;
	private readonly SettingsBrushPalette _palette;
	private readonly List<Swatch> _swatches = [];

	/// <summary>创建色块编辑器。</summary>
	public SwatchEditor(SettingsFieldViewModel field)
	{
		_field = field;
		_palette = new SettingsBrushPalette(this);
		Orientation = Orientation.Horizontal;
		ItemSpacing = NoriMetrics.Spacing2;
		LineSpacing = NoriMetrics.Spacing2;
		HorizontalAlignment = HorizontalAlignment.Left;
		Rebuild();
	}

	/// <inheritdoc />
	public void Refresh(string? propertyName)
	{
		switch (propertyName)
		{
			case null:
			case nameof(SettingsFieldViewModel.Options):
				Rebuild();
				break;
			case nameof(SettingsFieldViewModel.Label):
				SyncLabels();
				break;
			case nameof(SettingsFieldViewModel.Selected):
				SyncSelection();
				break;
			case nameof(SettingsFieldViewModel.IsReadOnly):
				IsEnabled = !_field.IsReadOnly;
				break;
		}
	}

	private void Rebuild()
	{
		Children.Clear();
		_swatches.Clear();
		foreach (SettingsOption option in _field.Options)
			_swatches.Add(Create(option));
		foreach (Swatch swatch in _swatches) Children.Add(swatch.Button);
		SyncSelection();
		IsEnabled = !_field.IsReadOnly;
	}

	private Swatch Create(SettingsOption option)
	{
		Ellipse dot = new()
		{
			Width = DotSize,
			Height = DotSize,
			Fill = Fill(option.Swatch),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Border ring = new()
		{
			Width = DotSize + RingThickness * 2,
			Height = DotSize + RingThickness * 2,
			CornerRadius = new CornerRadius((DotSize + RingThickness * 2) / 2),
			BorderThickness = new Thickness(RingThickness),
			HorizontalAlignment = HorizontalAlignment.Center,
			Child = dot,
		};
		TextBlock name = new()
		{
			Text = option.DisplayText,
			FontSize = NoriMetrics.FontXs,
			HorizontalAlignment = HorizontalAlignment.Center,
			TextAlignment = TextAlignment.Center,
		};
		StackPanel stack = new()
		{
			Spacing = NoriMetrics.Spacing1,
			HorizontalAlignment = HorizontalAlignment.Center,
			Children = { ring, name },
		};
		Button button = new()
		{
			Content = stack,
			Background = Brushes.Transparent,
			BorderBrush = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Padding = new Thickness(NoriMetrics.Spacing1),
			MinHeight = 0,
			MinWidth = 0,
			Focusable = true,
			HorizontalAlignment = HorizontalAlignment.Left,
			HorizontalContentAlignment = HorizontalAlignment.Center,
		};
		UseClearTemplate(button);
		string value = option.Value;
		button.Click += (_, _) => _field.Selected = value;
		ToolTip.SetTip(button, option.DisplayText);
		AutomationProperties.SetName(button, option.DisplayText);
		return new Swatch(value, button, ring, name);
	}

	private void SyncLabels()
	{
		for (int index = 0; index < _swatches.Count && index < _field.Options.Count; index++)
		{
			string text = _field.Options[index].DisplayText;
			_swatches[index].Name.Text = text;
			ToolTip.SetTip(_swatches[index].Button, text);
			AutomationProperties.SetName(_swatches[index].Button, text);
		}
	}

	private void SyncSelection()
	{
		IBrush primary = _palette["SettingsPrimaryBrush"];
		IBrush secondary = _palette["SettingsSecondaryBrush"];
		foreach (Swatch swatch in _swatches)
		{
			bool selected = swatch.Value == _field.Selected;
			swatch.Ring.BorderBrush = selected ? primary : Brushes.Transparent;
			swatch.Name.Foreground = selected ? primary : secondary;
		}
	}

	private static IBrush Fill(string? swatch)
	{
		if (swatch is not null && Color.TryParse(swatch, out Color color))
			return new SolidColorBrush(color);
		return NoriThemeTokens.Brush("bg-panel");
	}

	/// <summary>去掉主题按钮的底和描边，悬停样式也打不到这块模板上。</summary>
	private static void UseClearTemplate(Button button)
	{
		button.Template = new FuncControlTemplate<Button>((parent, _) =>
		{
			ContentPresenter presenter = new() { Background = Brushes.Transparent };
			presenter[!ContentPresenter.ContentProperty] = parent[!ContentControl.ContentProperty];
			presenter[!ContentPresenter.PaddingProperty] = parent[!TemplatedControl.PaddingProperty];
			presenter[!ContentPresenter.HorizontalContentAlignmentProperty] = parent[!ContentControl.HorizontalContentAlignmentProperty];
			presenter[!ContentPresenter.VerticalContentAlignmentProperty] = parent[!ContentControl.VerticalContentAlignmentProperty];
			return presenter;
		});
	}

	private sealed record Swatch(string Value, Button Button, Border Ring, TextBlock Name);
}
