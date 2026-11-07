using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Settings;

/// <summary>单选分段按钮。点已选项不会取消选中。</summary>
internal sealed class SegmentedEditor : WrapPanel, ISettingsFieldEditor
{
	private readonly SettingsFieldViewModel _field;
	private readonly List<ToggleButton> _buttons = [];
	private int _sync;

	/// <summary>创建分段编辑器。</summary>
	public SegmentedEditor(SettingsFieldViewModel field)
	{
		_field = field;
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
				SyncChecks();
				break;
			case nameof(SettingsFieldViewModel.IsReadOnly):
				IsEnabled = !_field.IsReadOnly;
				break;
		}
	}

	private void Rebuild()
	{
		Children.Clear();
		_buttons.Clear();
		foreach (SettingsOption option in _field.Options)
		{
			string value = option.Value;
			ToggleButton button = new()
			{
				Content = option.DisplayText,
				IsChecked = option.Value == _field.Selected,
				HorizontalAlignment = HorizontalAlignment.Left,
				MinHeight = 0,
			};
			button.Classes.Add("settings-choice");
			AutomationProperties.SetName(button, option.DisplayText);
			button.IsCheckedChanged += (_, _) => OnToggled(button, value);
			_buttons.Add(button);
			Children.Add(button);
		}
		IsEnabled = !_field.IsReadOnly;
	}

	private void OnToggled(ToggleButton button, string value)
	{
		if (_sync > 0) return;
		if (button.IsChecked == true)
		{
			_field.Selected = value;
			SyncChecks();
			return;
		}
		// ToggleButton 点击会先把已选项取消；单选不允许出现空选。
		if (_field.Selected != value) return;
		_sync++;
		button.IsChecked = true;
		_sync--;
	}

	private void SyncChecks()
	{
		if (_sync > 0) return;
		_sync++;
		try
		{
			for (int index = 0; index < _buttons.Count; index++)
			{
				bool selected = index < _field.Options.Count && _field.Options[index].Value == _field.Selected;
				_buttons[index].IsChecked = selected;
			}
		}
		finally
		{
			_sync--;
		}
	}

	private void SyncLabels()
	{
		for (int index = 0; index < _buttons.Count && index < _field.Options.Count; index++)
		{
			string text = _field.Options[index].DisplayText;
			_buttons[index].Content = text;
			AutomationProperties.SetName(_buttons[index], text);
		}
	}
}
