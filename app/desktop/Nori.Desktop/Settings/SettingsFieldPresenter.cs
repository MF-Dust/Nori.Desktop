using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Settings;

/// <summary>把设置字段模型呈现为统一的原生表单控件。</summary>
public sealed class SettingsFieldPresenter : ContentControl
{
	private SettingsFieldViewModel? _field;
	private TextBlock? _label;
	private TextBlock? _description;
	private TextBlock? _error;
	private TextBox? _textBox;
	private ToggleSwitch? _toggle;
	private NumericUpDown? _numeric;
	private Slider? _slider;
	private ComboBox? _combo;

	/// <summary>创建字段呈现器。</summary>
	public SettingsFieldPresenter()
	{
		DataContextChanged += OnDataContextChanged;
		AttachedToVisualTree += (_, _) => Build();
	}

	private void OnDataContextChanged(object? sender, EventArgs args)
	{
		if (_field is not null) _field.PropertyChanged -= OnFieldPropertyChanged;
		_field = DataContext as SettingsFieldViewModel;
		if (_field is null)
		{
			Content = null;
			return;
		}
		_field.PropertyChanged += OnFieldPropertyChanged;
		Build();
	}

	private void Build()
	{
		if (_field is null) return;
		_label = new TextBlock
		{
			Text = _field.Label,
			FontSize = 14,
			FontWeight = FontWeight.SemiBold,
			Foreground = Brush("SettingsPrimaryBrush"),
			VerticalAlignment = VerticalAlignment.Top,
		};
		_description = new TextBlock
		{
			Text = _field.Description,
			FontSize = 12,
			TextWrapping = TextWrapping.Wrap,
			Foreground = Brush("SettingsSecondaryBrush"),
			Margin = new Thickness(0, 4, 12, 0),
		};
		StackPanel labels = new() { Spacing = 1 };
		labels.Children.Add(_label);
		if (!string.IsNullOrWhiteSpace(_field.Description)) labels.Children.Add(_description);

		StackPanel editorStack = new() { Spacing = 4 };
		Control editor = BuildEditor();
		editorStack.Children.Add(editor);
		_error = new TextBlock
		{
			Text = _field.ErrorText,
			FontSize = 12,
			Foreground = Brush("SettingsErrorBrush"),
			TextWrapping = TextWrapping.Wrap,
			IsVisible = !string.IsNullOrWhiteSpace(_field.ErrorText),
		};
		editorStack.Children.Add(_error);

		Grid row = new()
		{
			ColumnDefinitions = new ColumnDefinitions
			{
				new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
				new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
			},
			ColumnSpacing = 18,
			Margin = new Thickness(0, 8, 0, 8),
		};
		Grid.SetColumn(labels, 0);
		Grid.SetColumn(editorStack, 1);
		row.Children.Add(labels);
		row.Children.Add(editorStack);
		Content = row;
	}

	private Control BuildEditor()
	{
		if (_field is null) return new Border();
		switch (_field.EditorKind)
		{
			case SettingsEditorKind.Boolean:
				_toggle = new ToggleSwitch
				{
					IsChecked = _field.Boolean,
					IsEnabled = !_field.IsReadOnly,
					HorizontalAlignment = HorizontalAlignment.Left,
				};
				_toggle.PropertyChanged += (_, args) =>
				{
					if (args.Property == ToggleSwitch.IsCheckedProperty) _field.Boolean = _toggle.IsChecked == true;
				};
				return _toggle;
			case SettingsEditorKind.Number:
				_numeric = new NumericUpDown
				{
					Value = (decimal)_field.Number,
					Minimum = (decimal)_field.Minimum,
					Maximum = (decimal)_field.Maximum,
					Increment = (decimal)_field.Increment,
					FormatString = "0.##",
					IsReadOnly = _field.IsReadOnly,
					HorizontalAlignment = HorizontalAlignment.Stretch,
				};
				_numeric.ValueChanged += (_, args) =>
				{
					if (args.NewValue is decimal value) _field.Number = (double)value;
				};
				return _numeric;
			case SettingsEditorKind.Slider:
				_slider = new Slider
				{
					Value = _field.Number,
					Minimum = _field.Minimum,
					Maximum = _field.Maximum,
					SmallChange = _field.Increment,
					LargeChange = Math.Max(_field.Increment * 5, _field.Increment),
					IsEnabled = !_field.IsReadOnly,
					HorizontalAlignment = HorizontalAlignment.Stretch,
				};
				_slider.ValueChanged += (_, args) => _field.Number = args.NewValue;
				return _slider;
			case SettingsEditorKind.Choice:
				_combo = new ComboBox
				{
					HorizontalAlignment = HorizontalAlignment.Stretch,
					IsEnabled = !_field.IsReadOnly,
				};
				UpdateOptions();
				_combo.SelectionChanged += (_, _) =>
				{
					if (_combo.SelectedIndex >= 0 && _combo.SelectedIndex < _field.Options.Count)
						_field.Selected = _field.Options[_combo.SelectedIndex].Value;
				};
				return _combo;
			case SettingsEditorKind.Action:
				Button button = new()
				{
					Content = _field.Label,
					HorizontalAlignment = HorizontalAlignment.Left,
					MinWidth = 132,
					Command = _field.Command,
				};
				return button;
			default:
				_textBox = new TextBox
				{
					Text = _field.Text,
					IsReadOnly = _field.IsReadOnly,
					AcceptsReturn = _field.EditorKind == SettingsEditorKind.Multiline,
					TextWrapping = _field.EditorKind == SettingsEditorKind.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
					MinHeight = _field.EditorKind == SettingsEditorKind.Multiline ? 72 : 34,
					PasswordChar = _field.EditorKind == SettingsEditorKind.Password ? '•' : '\0',
					PlaceholderText = _field.IsConfigured && _field.EditorKind == SettingsEditorKind.Password ? "••••••••" : null,
					HorizontalAlignment = HorizontalAlignment.Stretch,
				};
				_textBox.Bind(TextBox.TextProperty, new Binding(nameof(SettingsFieldViewModel.Text))
				{
					Mode = BindingMode.TwoWay,
					UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
				});
				return _textBox;
		}
	}

	private void UpdateOptions()
	{
		if (_combo is null || _field is null) return;
		_combo.ItemsSource = _field.Options.Select(option => option.DisplayText).ToArray();
		_combo.SelectedIndex = _field.Options.Select(option => option.Value).ToList().IndexOf(_field.Selected);
	}

	private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (_field is null) return;
		switch (args.PropertyName)
		{
			case nameof(SettingsFieldViewModel.Label):
				if (_label is not null) _label.Text = _field.Label;
				break;
			case nameof(SettingsFieldViewModel.Description):
				if (_description is not null) _description.Text = _field.Description;
				break;
			case nameof(SettingsFieldViewModel.ErrorText):
				if (_error is not null)
				{
					_error.Text = _field.ErrorText;
					_error.IsVisible = !string.IsNullOrWhiteSpace(_field.ErrorText);
				}
				break;
			case nameof(SettingsFieldViewModel.Text):
				if (_textBox is not null && _textBox.Text != _field.Text) _textBox.Text = _field.Text;
				break;
			case nameof(SettingsFieldViewModel.Boolean):
				if (_toggle is not null && _toggle.IsChecked != _field.Boolean) _toggle.IsChecked = _field.Boolean;
				break;
			case nameof(SettingsFieldViewModel.Number):
				if (_numeric is not null && _numeric.Value != (decimal)_field.Number) _numeric.Value = (decimal)_field.Number;
				if (_slider is not null && Math.Abs(_slider.Value - _field.Number) > 0.0001) _slider.Value = _field.Number;
				break;
			case nameof(SettingsFieldViewModel.Selected):
			case nameof(SettingsFieldViewModel.Options):
				if (_combo is not null)
				{
					if (args.PropertyName == nameof(SettingsFieldViewModel.Options)) UpdateOptions();
					else _combo.SelectedIndex = _field.Options.Select(option => option.Value).ToList().IndexOf(_field.Selected);
				}
				break;
		}
	}

	private IBrush Brush(string key) => SettingsBrushes.Resolve(this, key);
}
