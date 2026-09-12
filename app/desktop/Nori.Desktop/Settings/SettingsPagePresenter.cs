using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Settings;

/// <summary>设置页面的原生分组表单呈现器。</summary>
public sealed class SettingsPagePresenter : ContentControl
{
	private SettingsPageBase? _page;
	private TextBlock? _error;
	private TextBlock? _status;
	private NativeSettingsPagePresenter? _complex;

	/// <summary>创建页面呈现器。</summary>
	public SettingsPagePresenter()
	{
		DataContextChanged += OnDataContextChanged;
		AttachedToVisualTree += (_, _) => Build();
	}

	/// <summary>在绑定时序不确定时显式刷新当前页面。</summary>
	public void RefreshPage()
	{
		OnDataContextChanged(this, EventArgs.Empty);
	}

	private void OnDataContextChanged(object? sender, EventArgs args)
	{
		_complex?.Dispose();
		_complex = null;
		if (_page is not null) _page.PropertyChanged -= OnPagePropertyChanged;
		_page = DataContext as SettingsPageBase;
		if (_page is null)
		{
			Content = null;
			return;
		}
		if (_page is NativeSettingsPageBase native)
		{
			_complex = new NativeSettingsPagePresenter {DataContext = native};
			Content = _complex;
			return;
		}
		_page.PropertyChanged += OnPagePropertyChanged;
		Build();
	}

	private void Build()
	{
		// 复杂页面由专用呈现器负责，挂载时不能用空的普通分组覆盖它。
		if (_page is null or NativeSettingsPageBase) return;
		StackPanel root = new() { Spacing = 10, Margin = new Thickness(2, 0, 14, 20) };
		foreach (SettingsSectionViewModel section in _page.Sections)
		{
			Border card = new()
			{
				Background = Brush("SettingsCardBrush"),
				BorderBrush = Brush("SettingsBorderBrush"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(16, 12),
			};
			StackPanel content = new() { Spacing = 3 };
			content.Children.Add(new TextBlock
			{
				Text = section.Title,
				FontSize = 16,
				FontWeight = FontWeight.SemiBold,
				Foreground = Brush("SettingsPrimaryBrush"),
				Margin = new Thickness(0, 0, 0, 4),
			});
			foreach (SettingsFieldViewModel field in section.Fields)
				content.Children.Add(new SettingsFieldPresenter { DataContext = field });
			card.Child = content;
			root.Children.Add(card);
		}

		if (_page is ProactiveSettingsPage proactive)
		{
			root.Children.Add(BuildReminderList(proactive));
		}

		_error = new TextBlock
		{
			Text = _page.ErrorMessage,
			Foreground = Brush("SettingsErrorBrush"),
			TextWrapping = TextWrapping.Wrap,
			IsVisible = !string.IsNullOrWhiteSpace(_page.ErrorMessage),
			Margin = new Thickness(4, 2),
		};
		_status = new TextBlock
		{
			Text = _page.StatusMessage,
			Foreground = Brush("SettingsSecondaryBrush"),
			TextWrapping = TextWrapping.Wrap,
			IsVisible = !string.IsNullOrWhiteSpace(_page.StatusMessage),
			Margin = new Thickness(4, 0),
		};
		root.Children.Add(_error);
		root.Children.Add(_status);
		Content = root;
	}

	private Control BuildReminderList(ProactiveSettingsPage page)
	{
		ItemsControl items = new()
		{
			ItemsSource = page.Reminders,
			ItemTemplate = new FuncDataTemplate<ProactiveSettingsPage.ReminderItemViewModel>((item, _) =>
			{
				Grid row = new()
				{
					ColumnDefinitions = new ColumnDefinitions("*,Auto"),
					Margin = new Thickness(0, 4),
				};
				StackPanel details = new() { Spacing = 2 };
				details.Children.Add(new TextBlock { Text = item.Content, Foreground = Brush("SettingsPrimaryBrush") });
				details.Children.Add(new TextBlock { Text = item.TriggerAt.ToLocalTime().ToString("g"), Foreground = Brush("SettingsSecondaryBrush"), FontSize = 12 });
				Grid.SetColumn(details, 0);
				row.Children.Add(details);
				Button cancel = new() { Content = "取消 / Cancel", Command = item.CancelCommand, Margin = new Thickness(8, 0, 0, 0) };
				Grid.SetColumn(cancel, 1);
				row.Children.Add(cancel);
				return row;
			}),
			Margin = new Thickness(0, 4, 0, 0),
		};
		Border card = new()
		{
			Background = Brush("SettingsCardBrush"),
			BorderBrush = Brush("SettingsBorderBrush"),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(16, 12),
			Child = new StackPanel
			{
				Spacing = 4,
				Children =
				{
					new TextBlock { Text = "现有提醒 / Existing reminders", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("SettingsPrimaryBrush") },
					items,
				},
			},
		};
		return card;
	}

	private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (_page is null) return;
		if (args.PropertyName == nameof(SettingsPageBase.Sections))
		{
			Build();
			return;
		}
		if (args.PropertyName == nameof(SettingsPageBase.ErrorMessage) && _error is not null)
		{
			_error.Text = _page.ErrorMessage;
			_error.IsVisible = !string.IsNullOrWhiteSpace(_page.ErrorMessage);
		}
		if (args.PropertyName == nameof(SettingsPageBase.StatusMessage) && _status is not null)
		{
			_status.Text = _page.StatusMessage;
			_status.IsVisible = !string.IsNullOrWhiteSpace(_page.StatusMessage);
		}
	}

	private IBrush Brush(string key) => SettingsBrushes.Resolve(this, key);
}
