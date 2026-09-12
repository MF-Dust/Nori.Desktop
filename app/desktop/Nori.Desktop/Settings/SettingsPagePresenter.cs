using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
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
	private SettingsBrushPalette? _palette;

	/// <summary>创建页面呈现器。</summary>
	public SettingsPagePresenter()
	{
		DataContextChanged += OnDataContextChanged;
		AttachedToVisualTree += (_, _) => Build();
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
	}

	/// <summary>在绑定时序不确定时显式刷新当前页面。</summary>
	public void RefreshPage()
	{
		OnDataContextChanged(this, EventArgs.Empty);
	}

	private void OnDataContextChanged(object? sender, EventArgs args)
	{
		if (ReferenceEquals(_page, DataContext) && Content is not null) return;
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
		StackPanel root = new() { Spacing = 18, Margin = new Thickness(0) };
		foreach (SettingsSectionViewModel section in _page.Sections)
		{
			Border card = new()
			{
				Background = Brush("SettingsCardBrush"),
				BorderBrush = Brush("SettingsBorderBrush"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(12),
				Padding = new Thickness(20, 16),
			};
			card.Bind(IsVisibleProperty, new Binding(nameof(SettingsSectionViewModel.IsVisible)) {Source = section});
			StackPanel content = new() { Spacing = 0 };
			content.Children.Add(new TextBlock
			{
				Text = section.Title,
				FontSize = 15,
				FontWeight = FontWeight.SemiBold,
				Foreground = Brush("SettingsPrimaryBrush"),
				Margin = new Thickness(0, 0, 0, 14),
			});
			foreach (SettingsFieldViewModel field in section.Fields)
				content.Children.Add(new SettingsFieldPresenter { DataContext = field });
			card.Child = content;
			root.Children.Add(card);
		}

		if (_page is ProactiveSettingsPage proactive)
		{
			root.Children.Add(new ProactiveReminderList {DataContext = proactive});
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

	private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() => OnPagePropertyChanged(sender, args));
			return;
		}
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

	private IBrush Brush(string key) => (_palette ??= new SettingsBrushPalette(this))[key];

}
