using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nori.Desktop.Settings.Pages;

/// <summary>复杂列表型设置页的原生控件呈现器。</summary>
public sealed partial class NativeSettingsPagePresenter : ContentControl, IDisposable
{
	private NativeSettingsPageBase? _page;
	private SettingsPageViewModelBase? _viewModel;
	private StackPanel? _root;
	private SettingsBrushPalette? _palette;
	private bool _building;
	private bool _buildQueued;
	private bool _disposed;
	private TextBlock? _busyText;
	private TextBlock? _errorText;

	/// <summary>创建复杂设置页呈现器。</summary>
	public NativeSettingsPagePresenter()
	{
		DataContextChanged += OnDataContextChanged;
		SettingsLocalization.Changed += OnLanguageChanged;
		AttachedToVisualTree += (_, _) => Build();
	}

	private void OnLanguageChanged()
	{
		Dispatcher.UIThread.Post(() =>
		{
			_root = null;
			QueueBuild();
		});
	}

	private void OnDataContextChanged(object? sender, EventArgs args)
	{
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_viewModel.Changed -= OnViewModelChanged;
		}
		_root = null;
		_page = DataContext as NativeSettingsPageBase;
		_viewModel = _page?.ComplexViewModel;
		if (_viewModel is null)
		{
			Content = null;
			return;
		}
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;
		_viewModel.Changed += OnViewModelChanged;
		Build();
	}

	private void OnViewModelChanged() => QueueBuild();

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (_viewModel is DebugSettingsViewModel
			|| args.PropertyName is nameof(SettingsPageViewModelBase.IsBusy) or nameof(SettingsPageViewModelBase.ErrorMessage))
			QueueBuild();
	}

	private void QueueBuild()
	{
		if (_disposed || _buildQueued) return;
		_buildQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			_buildQueued = false;
			if (!_disposed) Build();
		});
	}

	private void Build()
	{
		if (_disposed || _viewModel is null || _building) return;
		// 诊断刷新保留控件树、筛选器焦点及两层滚动位置。
		if (_viewModel is DebugSettingsViewModel currentDebug && _root is not null)
		{
			UpdateDebug(currentDebug);
			return;
		}
		if (_root is not null && UpdateComplexPage()) return;
		_building = true;
		try
		{
			StackPanel root = new() {Spacing = 14, Margin = new Thickness(0)};
			_root = root;
			_busyText = new TextBlock
			{
				Text = NativeSettingsResources.Get("common.working"),
				Foreground = Brush("SettingsSecondaryBrush"),
				MinHeight = 20,
				Opacity = _viewModel.IsBusy ? 1 : 0,
			};
			_errorText = new TextBlock
			{
				Text = _viewModel.ErrorMessage,
				Foreground = Brush("SettingsErrorBrush"),
				TextWrapping = TextWrapping.Wrap,
				IsVisible = !string.IsNullOrWhiteSpace(_viewModel.ErrorMessage),
			};
			root.Children.Add(_busyText);
			root.Children.Add(_errorText);
			switch (_viewModel)
			{
				case SkillsSettingsViewModel skills:
					BuildSkills(root, skills);
					break;
				case McpSettingsViewModel mcp:
					BuildMcp(root, mcp);
					break;
				case AutomationSettingsViewModel automation:
					BuildAutomation(root, automation);
					break;
				case PluginsSettingsViewModel plugins:
					BuildPlugins(root, plugins);
					break;
				case DebugSettingsViewModel debug:
					BuildDebug(root, debug);
					break;
			}
			Content = root;
		}
		finally
		{
			_building = false;
		}
	}

	private async Task ExportDiagnosticsAsync(DebugSettingsViewModel viewModel)
	{
		DiagnosticExportItem? result = await viewModel.ExportDiagnosticsAsync().ConfigureAwait(true);
		if (result is not null) await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("debug.export"), $"{result.FileName}\n{result.Bytes} bytes").ConfigureAwait(true);
	}

	private async Task RunCrashAsync(DebugSettingsViewModel viewModel, string mode, bool mayExit)
	{
		string prompt = mayExit ? NativeSettingsResources.Get("debug.exitConfirm") : NativeSettingsResources.Get("debug.crashConfirm");
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("debug.crash"), prompt, true).ConfigureAwait(true)) await viewModel.TriggerCrashAsync(mode).ConfigureAwait(true);
	}

	private StackPanel CardBody(string title, string? subtitle)
	{
		StackPanel body = new() {Spacing = 4};
		body.Children.Add(new TextBlock {Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("SettingsPrimaryBrush")});
		if (!string.IsNullOrWhiteSpace(subtitle)) body.Children.Add(new TextBlock {Text = subtitle, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		return body;
	}

	private Border WrapCard(StackPanel body) => new()
	{
		Background = Brush("SettingsCardBrush"),
		BorderBrush = Brush("SettingsBorderBrush"),
		BorderThickness = new Thickness(1),
		CornerRadius = new CornerRadius(12),
		Padding = new Thickness(20, 16),
		Child = body,
	};

	private TextBlock Empty(string text) => new() {Text = text, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8)};

	private Button Button(string text, Action action, bool accent = false, bool danger = false, bool enabled = true)
	{
		Button button = new() {Content = text, IsEnabled = enabled, MinHeight = 32};
		if (accent) button.Classes.Add("accent");
		if (danger) button.Classes.Add("danger");
		button.Click += (_, _) => action();
		return button;
	}

	private async Task RunAsync(Func<Task> action)
	{
		try
		{
			if (_viewModel is not null) _viewModel.ClearError();
			await action().ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			_viewModel?.ReportError(exception);
		}
		finally
		{
			QueueBuild();
		}
	}

	private Window Owner() => TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("设置窗口尚未就绪");

	private IBrush Brush(string key) => (_palette ??= new SettingsBrushPalette(this))[key];


	/// <summary>解除页面和语言资源订阅。</summary>
	public void Dispose()
	{
		_disposed = true;
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_viewModel.Changed -= OnViewModelChanged;
		}
		SettingsLocalization.Changed -= OnLanguageChanged;
	}

	private static string CategoryName(string category) => category switch
	{
		"all" => NativeSettingsResources.Get("common.all"),
		"productivity" => SettingsLocalization.IsEnglish ? "Productivity" : "生产力",
		"coding" => SettingsLocalization.IsEnglish ? "Coding" : "编程",
		"life" => SettingsLocalization.IsEnglish ? "Life & learning" : "生活与学习",
		"roleplay" => SettingsLocalization.IsEnglish ? "Roleplay" : "情感与角色",
		"entertainment" => SettingsLocalization.IsEnglish ? "Entertainment" : "游戏与娱乐",
		_ => category,
	};

	private static IReadOnlyList<string> SplitCsv(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(item => item.Length > 0).ToArray();

}
