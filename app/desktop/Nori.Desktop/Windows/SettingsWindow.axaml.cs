using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Nori.Desktop.Bridge;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Windows;

/// <summary>跨平台原生设置窗口。</summary>
public partial class SettingsWindow : Window
{
	private readonly SettingsService _settingsService = null!;
	private readonly SettingsViewModel _viewModel = null!;
	private bool _closeInProgress;
	private bool _prepared;
	private CancellationTokenSource? _pageTransitionCts;

	/// <summary>供 Avalonia XAML 编译器使用的设计构造函数。</summary>
	public SettingsWindow()
	{
		InitializeComponent();
		PagePresenter.DataContextChanged += OnPageChanged;
	}

	/// <summary>创建设置窗口。</summary>
	public SettingsWindow(AppServices services) : this()
	{
		ArgumentNullException.ThrowIfNull(services);
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		_settingsService = new SettingsService(services, this);
		_viewModel = new SettingsViewModel(_settingsService);
		DataContext = _viewModel;
		AllowClose = false;
		Closing += OnClosing;
		Opened += (_, _) =>
		{
			PagePresenter.RefreshPage();
			_ = _viewModel.RefreshSnapshotAsync();
		};
	}

	/// <summary>允许窗口真正销毁。</summary>
	public bool AllowClose { get; set; }

	/// <summary>切换设置页。</summary>
	public void Navigate(string? page)
	{
		_viewModel.Navigate(page);
		PagePresenter.RefreshPage();
	}

	/// <summary>等待当前页面的防抖保存。</summary>
	public Task<bool> FlushPendingSavesAsync() => _viewModel.FlushPendingSavesAsync();

	/// <summary>关闭前取消查询、等待保存并释放订阅。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_prepared) return;
		await _viewModel.PrepareShutdownAsync().ConfigureAwait(false);
		_prepared = true;
		_settingsService.Dispose();
	}

	private async void OnPageChanged(object? sender, EventArgs args)
	{
		_pageTransitionCts?.Cancel();
		SettingsPageScroll.Offset = default;
		if (!IsVisible || PagePresenter.DataContext is not SettingsPageBase) return;

		// 只在换页时淡入，不移动表单或重建编辑状态；快速切换取消上一段动画。
		using CancellationTokenSource cancellation = new();
		_pageTransitionCts = cancellation;
		try
		{
			await new CrossFade(TimeSpan.FromMilliseconds(180))
			{
				FadeInEasing = new CubicEaseOut(),
				FillMode = FillMode.None,
			}.Start(null, SettingsPageBody, cancellation.Token);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		finally
		{
			if (ReferenceEquals(_pageTransitionCts, cancellation)) _pageTransitionCts = null;
		}
	}

	private void OnNavigateClick(object? sender, RoutedEventArgs args)
	{
		if (sender is Button {Tag: string page}) Navigate(page);
	}

	private async void OnClosing(object? sender, WindowClosingEventArgs args)
	{
		if (AllowClose) return;
		args.Cancel = true;
		if (_closeInProgress) return;
		_closeInProgress = true;
		try
		{
			bool saved = await FlushPendingSavesAsync().ConfigureAwait(true);
			if (saved && !AllowClose) Hide();
		}
		finally { _closeInProgress = false; }
	}

	protected override void OnClosed(EventArgs e)
	{
		_pageTransitionCts?.Cancel();
		PagePresenter.DataContextChanged -= OnPageChanged;
		// 真正关闭时解除呈现器的页面与全局语言订阅；隐藏窗口仍保留编辑上下文。
		DataContext = null;
		PagePresenter.RefreshPage();
		_viewModel?.Dispose();
		_settingsService?.Dispose();
		_prepared = true;
		base.OnClosed(e);
	}
}
