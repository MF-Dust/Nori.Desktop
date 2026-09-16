using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Logging;
using Nori.Core.Platform;
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
	{
		_services = services;
		Title = definition.Title;
		Width = definition.Width; Height = definition.Height;
		MinWidth = definition.MinWidth ?? definition.Width;
		MinHeight = definition.MinHeight ?? definition.Height;
		// 与 NoriWindow 同一套判断：能原生拖动就去掉系统边框（整个应用都是自绘 chrome，
		// 少设这一行就会在一堆无边框窗口里冒出一个系统标题栏）；不能拖的平台退回
		// 系统边框，不留一个既拖不动也没有提示的窗口。
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;

		_wizard = new FirstRunWizard(CompleteAsync);
		// 把窗口交给步骤层：选形象那一步要弹文件选择框，而它必须挂在一个窗口上。
		_steps = new FirstRunSteps(services, OnGate, Render, this);

		Content = BuildChrome();
		Render();

		Closing += (_, args) =>
		{
			if (AllowClose) return;
			args.Cancel = true;
			// 向导阶段关窗等于放弃安装，不留一个没配置完的应用在后台。
			_services.Windows.Shutdown();
		};
	}

	private bool IsEnglish() =>
		_services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN")
			.StartsWith("en", StringComparison.OrdinalIgnoreCase);

	private Control BuildChrome()
	{
		Button close = new()
		{
			Content = "✕", Width = 34, Height = 26,
			Background = Brushes.Transparent, Foreground = ChatPalette.Muted,
			BorderThickness = default,
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		close.Click += (_, _) => _services.Windows.Shutdown();

		Border header = new()
		{
			Height = 44,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel, BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(16, 0, 8, 0),
			Child = new Grid
			{
				ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
				Children =
				{
					Place(new StackPanel
					{
						Orientation = Orientation.Horizontal, Spacing = 8,
						VerticalAlignment = VerticalAlignment.Center,
						Children = {_pips, _stepLabel},
					}, 0),
					// 「3 / 5」那一条去掉了：左边已经有圆点（看得出位置）和步骤名
					// （看得出是哪一步），再写一遍数字是同一件事的第三种说法。
					Place(close, 2),
				},
			},
		};

		_back.Click += (_, _) => { _wizard.Prev(); Render(); };
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

		// 去掉系统边框之后，顶部这条就是拖动区 —— 向导有自己的头部，
		// 不像启动画面那样整面可拖。
		header.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(header).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
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
		if (_wizard.Snapshot().IsLast)
		{
			await _wizard.FinishAsync();
			Render();
			return;
		}

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
		if (_steps.SelectedModel.Length == 0)
			throw new InvalidOperationException(IsEnglish()
				? "No appearance selected"
				: "未选择形象，无法完成初始化");

		_services.Config.CompleteFirstRun(_steps.SelectedModel, _steps.TelemetryEnabled);
		_services.Telemetry.Configure(_steps.TelemetryEnabled);
		_services.Logger.Write(LogSource.Backend, "info",
			$"首次初始化完成: model={_steps.SelectedModel}");

		// 先置位再切窗口：初始化窗口若尚未就绪，可经这一位补跑，不会卡在转圈。
		if (_services.Runtime is { } runtime) runtime.MarkInitStartPending();
		cancellationToken.ThrowIfCancellationRequested();

		AllowClose = true;
		_services.Windows.Close(WindowLabels.FirstRun);
		_services.Windows.Show(WindowLabels.Init);
		await Task.CompletedTask;
	}

	/// <summary>把状态机的快照画出来。每次状态变化都整幅重画 —— 这一页够小。</summary>
	private void Render()
	{
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

		RenderFooter();
	}

	/// <summary>只重画导航与错误行。</summary>
	private void RenderFooter()
	{
		WizardState state = _wizard.Snapshot();
		bool english = IsEnglish();

		_back.Content = english ? "Back" : "上一步";
		_back.IsVisible = !state.IsFirst;
		_back.IsEnabled = state.CanPrev;

		bool submitting = state.FinishState == WizardFinishState.Submitting;
		_forward.Content = state.IsLast
			? submitting
				? english ? "Starting…" : "正在启动…"
				: state.FinishError.Length > 0
					? english ? "Retry" : "重试"
					: english ? "Start" : "开始使用"
			: english ? "Next" : "下一步";
		_forward.IsEnabled = state.IsLast ? !submitting : state.CanNext;
		// 显式设过 Background，Avalonia 的禁用态样式盖不掉 —— 不自己压暗的话，
		// 一颗按不动的按钮看起来和能按的一模一样。
		_forward.Opacity = _forward.IsEnabled ? 1 : 0.4;
		_back.Opacity = _back.IsEnabled ? 1 : 0.4;

		string message = state.StepError.Length > 0 ? state.StepError : state.FinishError;
		_error.Text = message;
		_error.IsVisible = message.Length > 0;
	}

	// ── 测试用 ─────────────────────────────────────────────────────────────

	/// <summary>当前处在哪一步。</summary>
	internal WizardStep CurrentStepForTests => _wizard.Snapshot().Step;

	/// <summary>底部那条错误行上现在写着什么。</summary>
	internal string ErrorTextForTests => _error.Text ?? "";

	/// <summary>前进按钮的文案与可用性 —— 末步会换成「开始使用」。</summary>
	internal (string Text, bool Enabled) ForwardForTests => (_forward.Content as string ?? "", _forward.IsEnabled);

	/// <summary>推进一步，走的是按钮那条路。</summary>
	internal Task AdvanceForTests() => AdvanceAsync();

	/// <summary>
	/// 直接跳到某一步，绕开守卫。
	///
	/// **只给测试用。** 选形象那一步在测试环境里必然被自己挡住（一个模型都没装），
	/// 而末步和完成流程仍然要能测到。
	/// </summary>
	internal void ForceStepForTests(WizardStep? target = null)
	{
		WizardStep want = target ?? FirstRunWizard.Order[
			Math.Min(FirstRunWizard.Order.Count - 1, _wizard.Snapshot().Index + 1)];
		while (_wizard.Snapshot().Step != want && !_wizard.Snapshot().IsLast)
		{
			_wizard.ClearStep();
			if (!_wizard.Next()) break;
		}
		Render();
	}

	/// <summary>指定选中的形象。**只给测试用** —— 测试环境里一个模型都没装。</summary>
	internal void SelectModelForTests(string modelId) => _steps.SelectModelForTests(modelId);

	/// <summary>后退一步。</summary>
	internal void BackForTests()
	{
		_wizard.Prev();
		Render();
	}
}
