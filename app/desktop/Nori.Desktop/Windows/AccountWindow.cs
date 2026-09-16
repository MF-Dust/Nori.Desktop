using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Cloud;
using Nori.Core.Platform;
using Nori.Desktop.Account;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Windows;

/// <summary>请求结果。窗口据此决定后续状态与动效。</summary>
public sealed record SignInOutcome
{
	/// <summary>成功。密码方式为登录成功，验证码方式为验证码校验通过。</summary>
	public required bool Ok { get; init; }

	/// <summary>本次仅完成验证码发送，尚未登录。</summary>
	public bool CodeSent { get; init; }

	/// <summary>失败原因。需可直接展示：「验证失败」这类描述无法指明应修改哪一项。</summary>
	public string Error { get; init; } = "";
}

/// <summary>
/// 账户窗口。
///
/// 登录为可选项：不登录时本地功能完整可用。因此本窗口不具有阻断性 —— 关闭即等同于
/// 选择不登录，不会阻塞后续流程。
///
/// 外壳只负责窗口层（自绘标题栏、拖动、Esc、显示与隐藏），判断逻辑在
/// <see cref="SignInForm"/>，渲染与动效在 <see cref="SignInView"/>，
/// 与 FirstRunWindow / FirstRunSteps 的分工一致。
/// </summary>
public sealed class AccountWindow : Window
{
	private readonly SignInForm _form;
	private readonly SignInView _view;
	private readonly Func<SignInState, Task<SignInOutcome>> _submit;
	private readonly Action<bool> _onClosed;

	private bool _signedIn;

	/// <param name="submit">把一次提交递出去。窗口不认识网络。</param>
	/// <param name="onClosed">窗口关掉时喊一次，参数是「登录成功了没有」。</param>
	/// <param name="initialMethod">本机上次使用的登录方式。见 SignInForm 的构造函数。</param>
	public AccountWindow(
		Func<SignInState, Task<SignInOutcome>> submit,
		Action<bool> onClosed,
		SignInMethod initialMethod = SignInMethod.Code)
	{
		_form = new SignInForm(initialMethod);
		_submit = submit;
		_onClosed = onClosed;

		Title = "Nori 账户";
		Width = 460;
		Height = 600;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		// 与其余原生窗口同一套控件主题。输入框的焦点、按钮的交互态因此和首次运行、
		// 设置窗一模一样 —— 这扇窗不该有自己的一套外观。
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;

		// 与其余原生窗口同一套判断：能原生拖动就摘掉系统边框（整个应用都是自绘
		// chrome，少设这一行就会在一堆无边框窗口里冒出一个系统标题栏）；不能拖的
		// 平台退回系统边框，不留一个既拖不动也没有提示的窗口。
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;

		_view = new SignInView(_form, OnSubmit, Dismiss, FinishSuccess);

		// 底部留一行给「标识 + Nyco Cloud Network」：这扇窗唯一的用途就是登录，
		// 而登的是 NCN 的账户 —— 出处该说在动作发生的地方，不是藏进关于页。
		Grid root = new() {RowDefinitions = new RowDefinitions("Auto,*,Auto")};
		Control chrome = BuildChrome();
		Control badge = Account.PoweredByNcn.Build();
		badge.Margin = new Thickness(0, 0, 0, 16);
		root.Children.Add(chrome);
		root.Children.Add(_view);
		root.Children.Add(badge);
		Grid.SetRow(chrome, 0);
		Grid.SetRow(_view, 1);
		Grid.SetRow(badge, 2);
		Content = root;

		Opened += (_, _) => Dispatcher.UIThread.Post(_view.Activate);
		Closed += (_, _) => { _view.Deactivate(); _onClosed(_signedIn); };
		KeyDown += (_, args) =>
		{
			if (args.Key != Key.Escape) return;
			args.Handled = true;
			Dismiss();
		};
	}

	/// <summary>
	/// 标题栏。
	///
	/// 只有标题与关闭按钮，不提供最小化与最大化：本窗口只有「完成登录」与「关闭」
	/// 两种出口，其余按钮无对应用途。
	/// </summary>
	private Control BuildChrome()
	{
		TextBlock title = new()
		{
			Text = "账户",
			FontSize = 12, FontWeight = FontWeight.SemiBold,
			Foreground = ChatPalette.Accent,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Button close = new()
		{
			Content = "✕",
			Width = 40, Height = 40,
			Background = Brushes.Transparent,
			BorderThickness = default,
			Foreground = ChatPalette.Muted,
			FontSize = 12,
			HorizontalAlignment = HorizontalAlignment.Right,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		close.Click += (_, _) => Dismiss();

		Border bar = new()
		{
			Height = 40,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(20, 0, 0, 0),
			Child = new Grid {Children = {title, close}},
		};

		// 去掉系统边框后需自行处理拖动。只有标题栏可拖：整窗可拖会与输入框内的
		// 文本拖选冲突。
		bar.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
		};
		return bar;
	}

	/// <summary>
	/// 提交。
	///
	/// 窗口不直接访问网络：把当前快照交给调用方，用返回结果推进状态机。
	/// 登录流程因此可在无服务端的条件下完整测试。
	/// </summary>
	private async void OnSubmit(SignInState state)
	{
		SignInOutcome outcome;
		try
		{
			outcome = await _submit(state);
		}
		catch (Exception error)
		{
			// 调用方可能抛出异常。视为一次失败处理，不能让窗口停留在「正在登录」。
			outcome = new SignInOutcome {Ok = false, Error = Describe(error)};
		}

		if (!outcome.Ok)
		{
			_form.Fail(outcome.Error);
			_view.Refresh();
			return;
		}

		if (outcome.CodeSent)
		{
			_form.CodeDelivered();
			_view.Refresh();
			return;
		}

		_signedIn = true;
		_form.Succeed();
		_view.PlaySuccess();
	}

	/// <summary>把异常转换成可展示的说明。原始异常文本对用户没有意义。</summary>
	private static string Describe(Exception error) => error switch
	{
		TaskCanceledException or TimeoutException => "服务器无响应，请稍后重试",
		HttpRequestException => "网络不可用，请检查网络连接后重试",
		_ => "登录失败，请稍后重试",
	};

	/// <summary>视觉测试用：设置表单状态后截图。</summary>
	internal SignInForm FormForTests => _form;

	/// <summary>视觉测试用：状态设置完成后触发一次重绘。</summary>
	internal void RefreshForTests() => _view.Refresh();

	/// <summary>视觉测试用：切到已连接档位，不关闭窗口。</summary>
	internal void ConnectForTests() => _view.ConnectForTests();

	/// <summary>视觉测试用：写入控件文本。</summary>
	internal void FillForTests(string email, string password, string code) =>
		_view.FillForTests(email, password, code);

	/// <summary>成功动效结束后关闭窗口。</summary>
	private void FinishSuccess() => Close();

	/// <summary>
	/// 关闭窗口。
	///
	/// 关闭即选择不登录，不做二次确认：云端服务本身是可选项，为可选项增加确认会让它
	/// 看起来像一次误操作。
	/// </summary>
	private void Dismiss() => Close();
}
