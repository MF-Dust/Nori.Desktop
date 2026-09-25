using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.FirstRun;

namespace Nori.Desktop.Windows;

/// <summary>
/// 原生首次运行向导。
///
/// 迁移的第二块（初始化窗口之后）。同样不碰音频 —— 音频宿主在主界面那个 WebView 里，
/// 那一块要单独处理。
///
/// 壳只做三件事：顶部的步骤指示、中间的舞台、底部的导航。每一步自己是什么样、
/// 要调什么，全在 <see cref="FirstRunSteps"/> 那边；步进与守卫在
/// <see cref="FirstRunWizard"/>（Nori.Core，可单测，不碰 Avalonia）。
///
/// 这个三层划分是从 Vue 版照搬的，包括它当初解决的问题：更早的实现里失败只往
/// 控制台打一行，界面毫无变化，用户看到的就是「卡住了」。所以每一步的失败都要
/// 落到底部那条错误行上，并且允许重试。
/// </summary>
public sealed class FirstRunWindow : Window
{
	private readonly AppServices _services;
	private readonly FirstRunWizard _wizard;
	private readonly FirstRunSteps _steps;
	private readonly CancellationTokenSource _lifetime;
	private readonly CancellationToken _lifetimeToken;
	private bool _closed;

	private readonly StackPanel _pips = new()
	{
		Orientation = Orientation.Horizontal, Spacing = 6,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly TextBlock _stepLabel = new()
	{
		Foreground = ChatPalette.Accent, FontSize = 12, FontWeight = FontWeight.SemiBold,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly TextBlock _counter = new()
	{
		Foreground = ChatPalette.Faint, FontSize = 12,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly ContentControl _stage = new() {Margin = new Thickness(28, 18)};
	private readonly Button _back = new();
	private readonly Button _forward = new();
	private readonly TextBlock _error = new()
	{
		Foreground = ChatPalette.Danger, FontSize = 12,
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Center,
		TextWrapping = TextWrapping.NoWrap,
	};

	/// <summary>仅宿主退出流程可允许真正关闭。</summary>
	public bool AllowClose { get; set; }

	public FirstRunWindow(WindowDefinition definition, AppServices services)
		: this(definition, services, null) { }

	internal FirstRunWindow(WindowDefinition definition, AppServices services, Func<string, Task<string?>>? pickModel)
	{
		_services = services;
		Title = definition.Title;
		Width = definition.Width; Height = definition.Height;
		MinWidth = definition.MinWidth ?? definition.Width;
		MinHeight = definition.MinHeight ?? definition.Height;
		CanResize = definition.CanResize;
		NativeWindowSizing.ConstrainOnFirstOpen(this, NativeWindowSizing.FirstRunSize);
		WindowDecorations = WindowDecorations.None;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;

		_wizard = new FirstRunWizard(CompleteAsync);
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(services.ShutdownToken);
		_lifetimeToken = _lifetime.Token;
		_steps = new FirstRunSteps(services, OnGate, Render, pickModel ?? PickModelAsync, _lifetimeToken);

		Content = BuildChrome();
		Render();
		Closed += (_, _) =>
		{
			_closed = true;
			// 关闭窗口立即终止导入等待，不依赖应用稍后才触发的全局退出信号。
			_lifetime.Cancel();
			_lifetime.Dispose();
		};

		Closing += (_, args) =>
		{
			if (AllowClose) return;
			args.Cancel = true;
			// 向导阶段关窗等于放弃安装，不留一个没配置完的应用在后台。
			_services.Windows.Shutdown();
		};
	}

	private async Task<string?> PickModelAsync(string sourceKind)
	{
		if (sourceKind == "folder")
		{
			IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
			{
				Title = IsEnglish() ? "Choose a Live2D folder" : "选择 Live2D 模型文件夹",
				AllowMultiple = false,
			});
			return folders.Count > 0 ? folders[0].TryGetLocalPath()
				?? throw new InvalidOperationException("请选择本地模型文件夹") : null;
		}

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = IsEnglish() ? "Choose a Live2D ZIP" : "选择 Live2D 资源文件 (.zip)",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("Live2D ZIP") {Patterns = ["*.zip"]}],
		});
		return files.Count > 0 ? files[0].TryGetLocalPath()
			?? throw new InvalidOperationException("请选择本地模型 ZIP 文件") : null;
	}

	private bool IsEnglish() =>
		_services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN")
			.StartsWith("en", StringComparison.OrdinalIgnoreCase);

	private Control BuildChrome()
	{
		Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
		heading.Children.Add(Place(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _pips, _stepLabel } }, 0, HorizontalAlignment.Left));
		heading.Children.Add(Place(_counter, 1, HorizontalAlignment.Right));
		NativeWindowChrome header = new(this, IsEnglish, heading) { Height = 44 };

		_back.Click += (_, _) => Back();
		_forward.Click += (_, _) => _ = AdvanceAsync();
		StyleNav(_back, primary: false);
		StyleNav(_forward, primary: true);

		Border footer = new()
		{
			Height = 56,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel, BorderThickness = new Thickness(0, 1, 0, 0),
			Padding = new Thickness(20, 0),
			Child = new Grid
			{
				ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
				Children = {Place(_back, 0), Place(_error, 1), Place(_forward, 2)},
			},
		};

		return new DockPanel
		{
			LastChildFill = true,
			Children =
			{
				Dock(header, Avalonia.Controls.Dock.Top),
				Dock(footer, Avalonia.Controls.Dock.Bottom),
				_stage,
			},
		};
	}

	private static Control Place(Control control, int column,
		HorizontalAlignment alignment = HorizontalAlignment.Center)
	{
		Grid.SetColumn(control, column);
		control.HorizontalAlignment = alignment;
		control.VerticalAlignment = VerticalAlignment.Center;
		return control;
	}

	private static Control Dock(Control control, Dock side)
	{
		DockPanel.SetDock(control, side);
		return control;
	}

	private static void StyleNav(Button button, bool primary)
	{
		button.Padding = new Thickness(primary ? 22 : 16, 7);
		button.CornerRadius = new CornerRadius(8);
		button.Background = primary ? ChatPalette.Teal : ChatPalette.Panel;
		button.Foreground = primary ? ChatPalette.OnTeal : ChatPalette.Body;
		button.BorderThickness = default;
	}

	/// <summary>
	/// 某一步报了阻断或解除。
	///
	/// **只刷底部**，不重建舞台 —— AI 那一步每敲一个键都会走到这里，重建会把输入框
	/// 换掉，焦点和光标当场丢。要改变这一页长相的交互（选形象、换语言）走
	/// <see cref="Render"/>。
	/// </summary>
	private void OnGate(string error)
	{
		if (_closed) return;
		if (error.Length > 0) _wizard.BlockStep(error);
		else _wizard.ClearStep();
		RenderFooter();
	}

	/// <summary>
	/// 前进。
	///
	/// 离开 AI 那一步时先落盘：填了内容就存，存失败停在原地并把错误摆到底部。
	/// 每次离开都重算「存过没有」—— 用户可能回头把填过的内容清空。
	/// </summary>
	private async Task AdvanceAsync()
	{
		if (_closed || _steps.IsImporting) return;
		if (_wizard.Snapshot().IsLast)
		{
			Task<bool> finishing = _wizard.FinishAsync(_lifetimeToken);
			RenderFooter();
			await finishing;
			Render();
			return;
		}

		// 模型可能在停留期间被移除，离开前重新检查安装状态。
		if (_wizard.Snapshot().Step == WizardStep.Model) Render();
		if (_wizard.Snapshot().Step == WizardStep.Ai)
		{
			_forward.IsEnabled = false;
			try
			{
				if (!await _steps.SaveAiDraftAsync())
				{
					_wizard.BlockStep(IsEnglish() ? "Failed to save model provider" : "保存模型服务失败");
					Render();
					return;
				}
			}
			finally
			{
				_forward.IsEnabled = true;
			}
		}

		_wizard.Next();
		Render();
	}

	private async Task CompleteAsync(CancellationToken cancellationToken)
	{
		// 自己先挡一道。没有形象时 CompleteFirstRun 会抛 ArgumentException，而状态机
		// 把异常消息原样摆到底部那条错误行上 —— 用户会看见「模型 ID 不能为空」这种
		// 内部说法。正常路径上选形象那一步就挡住了，这里是兜底。
		if (_steps.SelectedModel.Length == 0
			|| !await Task.Run(() => _services.Resources.IsInstalled(ResourceType.Live2D, _steps.SelectedModel), cancellationToken))
			throw new InvalidOperationException(IsEnglish()
				? "Choose an appearance before starting"
				: "开始之前要先选一个形象");

		if (_closed) throw new OperationCanceledException("首次运行向导已关闭");
		cancellationToken.ThrowIfCancellationRequested();
		_services.Config.CompleteFirstRun(_steps.SelectedModel, _steps.TelemetryEnabled);
		_services.Telemetry.Configure(_steps.TelemetryEnabled);
		_services.Logger.Write(LogSource.Backend, "info",
			$"首次初始化完成: model={_steps.SelectedModel}");

		// 先置位再切窗口：初始化窗口若尚未就绪，可经这一位补跑，不会卡在转圈。
		if (_services.Runtime is { } runtime) runtime.MarkInitStartPending();
		cancellationToken.ThrowIfCancellationRequested();

		_services.Windows.Close(WindowLabels.FirstRun);
		_services.Windows.Show(WindowLabels.Init);
	}

	/// <summary>把状态机的快照画出来。每次状态变化都整幅重画 —— 这一页够小。</summary>
	private void Render()
	{
		if (_closed) return;
		WizardState state = _wizard.Snapshot();
		bool english = IsEnglish();
		RenderChrome(state, english);
	}

	/// <summary>整幅重画：步骤变了、语言变了、或者这一页的长相要变。</summary>
	private void RenderChrome(WizardState state, bool english)
	{

		_stage.Content = _steps.Build(state.Step, english);
		// 构建完才读这一步自己的门。写在构建过程中回调会递归 —— 见 FirstRunSteps.Gate。
		if (_steps.Gate.Length > 0) _wizard.BlockStep(_steps.Gate);
		else _wizard.ClearStep();
		state = _wizard.Snapshot();

		_pips.Children.Clear();
		for (int index = 0; index < FirstRunWizard.Order.Count; index++)
		{
			bool done = index < state.Index;
			bool current = index == state.Index;
			_pips.Children.Add(new Ellipse
			{
				Width = current ? 8 : 6, Height = current ? 8 : 6,
				Fill = current ? ChatPalette.Accent : done ? ChatPalette.Teal : ChatPalette.Faint,
				Opacity = current || done ? 1 : 0.45,
				VerticalAlignment = VerticalAlignment.Center,
			});
		}
		_stepLabel.Text = FirstRunSteps.Title(state.Step, english);
		_counter.Text = $"{state.Index + 1} / {FirstRunWizard.Order.Count}";

		RenderFooter();
	}

	/// <summary>只重画导航与错误行。</summary>
	private void RenderFooter()
	{
		WizardState state = _wizard.Snapshot();
		bool english = IsEnglish();

		_back.Content = english ? "Back" : "上一步";
		_back.IsVisible = !state.IsFirst;
		_back.IsEnabled = state.CanPrev && !_steps.IsImporting;

		bool submitting = state.FinishState == WizardFinishState.Submitting;
		_forward.Content = state.IsLast
			? submitting
				? english ? "Starting..." : "正在启动..."
				: state.FinishError.Length > 0
					? english ? "Retry" : "重试"
					: english ? "Start" : "开始使用"
			: english ? "Next" : "下一步";
		_forward.IsEnabled = !_steps.IsImporting && (state.IsLast ? !submitting : state.CanNext);
		// 显式设过 Background，Avalonia 的禁用态样式盖不掉 —— 不自己压暗的话，
		// 一颗按不动的按钮看起来和能按的一模一样。
		_forward.Opacity = _forward.IsEnabled ? 1 : 0.4;
		_back.Opacity = _back.IsEnabled ? 1 : 0.4;

		string message = state.StepError.Length > 0 ? state.StepError : state.FinishError;
		_error.Text = message;
		_error.IsVisible = message.Length > 0;
	}

	private void Back()
	{
		_wizard.Prev();
		Render();
	}
}
