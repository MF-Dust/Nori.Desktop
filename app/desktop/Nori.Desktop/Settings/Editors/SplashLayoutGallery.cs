using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Settings;

/// <summary>启动画面布局卡片。按宽度增加列数，选中项用强调色描边。</summary>
internal sealed class SplashLayoutGallery : Grid, ISettingsFieldEditor
{
	private const double MinCardWidth = 150;
	private const double Gap = NoriMetrics.Spacing3;

	private readonly SettingsFieldViewModel _field;
	private readonly SettingsBrushPalette _palette;
	private readonly List<Card> _cards = [];
	private int _columns = -1;

	/// <summary>创建布局画廊。</summary>
	public SplashLayoutGallery(SettingsFieldViewModel field)
	{
		_field = field;
		_palette = new SettingsBrushPalette(this);
		ColumnSpacing = Gap;
		RowSpacing = Gap;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		SizeChanged += (_, args) => UpdateColumns(args.NewSize.Width);
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
				ApplyReadOnly();
				break;
		}
	}

	/// <inheritdoc />
	protected override Size MeasureOverride(Size availableSize)
	{
		UpdateColumns(double.IsFinite(availableSize.Width) ? availableSize.Width : 0);
		return base.MeasureOverride(availableSize);
	}

	private void Rebuild()
	{
		Children.Clear();
		_cards.Clear();
		_columns = -1;
		foreach (SettingsOption option in _field.Options)
		{
			Card card = Create(option);
			_cards.Add(card);
			Children.Add(card.Button);
		}
		UpdateColumns(Bounds.Width);
		SyncSelection();
		ApplyReadOnly();
	}

	private Card Create(SettingsOption option)
	{
		SplashLayoutThumbnail thumbnail = new(option.Value)
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		TextBlock caption = new()
		{
			Text = option.DisplayText,
			FontSize = NoriMetrics.FontXs,
			Foreground = _palette["SettingsPrimaryBrush"],
			HorizontalAlignment = HorizontalAlignment.Center,
			TextAlignment = TextAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
		};
		StackPanel body = new()
		{
			Spacing = NoriMetrics.Spacing1,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Children = { thumbnail, caption },
		};
		Border frame = new()
		{
			Background = _palette["SettingsCardBrush"],
			CornerRadius = new CornerRadius(NoriMetrics.RadiusMd),
			ClipToBounds = true,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Child = body,
		};
		Button button = new()
		{
			Content = frame,
			Padding = new Thickness(0),
			MinHeight = 0,
			MinWidth = 0,
			Background = Brushes.Transparent,
			BorderBrush = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Opacity = 1,
			Focusable = true,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
		};
		UseClearTemplate(button);
		string value = option.Value;
		button.Click += (_, _) => _field.Selected = value;
		AutomationProperties.SetName(button, option.DisplayText);
		return new Card(value, button, frame, caption);
	}

	private void SyncLabels()
	{
		for (int index = 0; index < _cards.Count && index < _field.Options.Count; index++)
		{
			string text = _field.Options[index].DisplayText;
			_cards[index].Caption.Text = text;
			AutomationProperties.SetName(_cards[index].Button, text);
		}
	}

	private void SyncSelection()
	{
		IBrush accent = _palette["SettingsAccentBrush"];
		IBrush border = _palette["SettingsBorderBrush"];
		foreach (Card card in _cards)
		{
			bool selected = card.Value == _field.Selected;
			card.Frame.BorderBrush = selected ? accent : border;
			// 1px 与 2px 切换时用内边距补齐，卡片内容不跳动。
			double thickness = selected ? 2 : 1;
			card.Frame.BorderThickness = new Thickness(thickness);
			card.Frame.Padding = new Thickness(NoriMetrics.Spacing2 + (2 - thickness));
		}
	}

	private void ApplyReadOnly()
	{
		bool readOnly = _field.IsReadOnly;
		IsEnabled = !readOnly;
		Opacity = readOnly ? NoriMetrics.OpacityDisabled : 1;
	}

	private void UpdateColumns(double width)
	{
		if (_cards.Count == 0)
		{
			if (_columns == 0) return;
			_columns = 0;
			ColumnDefinitions.Clear();
			RowDefinitions.Clear();
			return;
		}
		int columns = width <= 0
			? 1
			: Math.Clamp((int)Math.Floor((width + Gap) / (MinCardWidth + Gap)), 1, _cards.Count);
		if (columns == _columns && ColumnDefinitions.Count == columns) return;
		_columns = columns;
		ColumnDefinitions.Clear();
		RowDefinitions.Clear();
		for (int column = 0; column < columns; column++)
			ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		for (int index = 0; index < _cards.Count; index++)
		{
			if (index % columns == 0) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			SetColumn(_cards[index].Button, index % columns);
			SetRow(_cards[index].Button, index / columns);
		}
	}

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

	private sealed record Card(string Value, Button Button, Border Frame, TextBlock Caption);
}
