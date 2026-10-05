using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Cloud;
using Nori.Desktop.Chat;
using Nori.Desktop.Ui;

namespace Nori.Desktop.Account;

/// <summary>
/// 登录界面。
///
/// ── 视觉规范 ──────────────────────────────────────────────────────────────
/// 与首次运行向导保持一致：居中单列、圆角、标签置于输入上方、主按钮为青绿填充并
/// 右对齐。配色全部取自 <see cref="ChatPalette"/>，不引入新色值 —— 青 (#7de3ff)
/// 用于标题与次级入口，青绿 (#5eead4) 用于主操作，红 (#ff6b72) 用于错误。
///
/// ── 状态指示：复用品牌标记 ────────────────────────────────────────────────
/// <see cref="NoriHalo"/> 是启动画面使用的品牌标记（四瓣标志 + 两圈虚线 + 光晕）。
/// 这里复用它表示登录状态，不另建指示器：
///
///   未登录     虚线压暗、标志压暗
///   条件齐备   虚线转强调色、标志全亮
///   请求中     双环反向旋转、标志缩放、光晕脉动
///   已登录     外环转青绿实线、光晕全开
///
/// 虚线转实线是本窗口唯一的形状变化，仅用于登录成功。
///
/// ── 其余动效 ──────────────────────────────────────────────────────────────
/// 无。输入框焦点、按钮悬停与按下状态全部由应用既有的控件主题提供，另行实现会导致
/// 本窗口与其余窗口外观不一致。
/// </summary>
internal sealed class SignInView : Panel
{
	private const double HaloSize = 118;
	private const double Column = 300;

	private readonly SignInForm _form;
	private readonly Action<SignInState> _onSubmit;
	private readonly Action _onSkip;
	private readonly Action _onDone;

	private readonly NoriHalo _halo = new(HaloSize);
	private readonly TextBox _email;
	private readonly TextBox _password;
	private readonly TextBox _code;
	private readonly StackPanel _passwordBlock;
	private readonly StackPanel _codeBlock;
	private readonly Button _submit;
	private readonly Button _switchMethod;
	private readonly Button _resend;
	private readonly TextBlock _lede;
	private readonly TextBlock _line;

	private DispatcherTimer? _seconds;
	private DispatcherTimer? _closing;

	internal SignInView(SignInForm form, Action<SignInState> onSubmit, Action onSkip, Action onDone)
	{
		_form = form;
		_onSubmit = onSubmit;
		_onSkip = onSkip;
		_onDone = onDone;

		_email = Field("you@example.com", machineText: true);
		_password = Field("至少 12 个字符");
		_password.PasswordChar = '•';
		_code = Field("6 位数字", machineText: true);
		_code.MaxLength = SignInForm.CodeLength;
		_code.FontSize = 17;
		_code.LetterSpacing = 5;

		_email.TextChanged += (_, _) => { _form.SetEmail(_email.Text ?? ""); Paint(); };
		_password.TextChanged += (_, _) => { _form.SetPassword(_password.Text ?? ""); Paint(); };
		_code.TextChanged += (_, _) =>
		{
			_form.SetCode(_code.Text ?? "");
			// 非数字立即丢弃，不在提交时才报格式错误。
			if (_code.Text != _form.State.Code) _code.Text = _form.State.Code;
			Paint();
		};

		_lede = new TextBlock
		{
			FontSize = 12.5, Foreground = ChatPalette.Muted,
			TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
			MaxWidth = Column, LineHeight = 21,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 8, 0, 0),
		};

		_submit = Pill("登录", primary: true);
		_submit.Click += (_, _) => Submit();

		_switchMethod = Quiet();
		_switchMethod.Click += (_, _) => SwitchMethod();

		_resend = Quiet();
		_resend.Click += (_, _) => Submit(force: true);

		_passwordBlock = Block("密码", _password);
		_codeBlock = Block("验证码", _code);

		_line = new TextBlock
		{
			FontSize = 11.5, Foreground = ChatPalette.Faint,
			TextWrapping = TextWrapping.Wrap, LineHeight = 18,
			MaxWidth = Column, Margin = new Thickness(0, 12, 0, 0),
		};

		Children.Add(BuildLayout());
		Paint();
	}

	/// <summary>窗口显示时调用。</summary>
	internal void Activate()
	{
		_email.Focus();
		_seconds ??= new DispatcherTimer {Interval = TimeSpan.FromSeconds(1)};
		_seconds.Tick -= OnSecond;
		_seconds.Tick += OnSecond;
		_seconds.Start();
		if (MotionPreference.AllowAnimation) _halo.Start();
	}

	/// <summary>窗口隐藏时调用。停止全部定时器，避免后台空转。</summary>
	internal void Deactivate()
	{
		_seconds?.Stop();
		_closing?.Stop();
		_halo.Stop();
	}

	/// <summary>请求回来了，重画一次。</summary>
	internal void Refresh() => Paint();

	/// <summary>
	/// 登录成功。
	///
	/// 光环切到已连接档位，保持 700 毫秒供用户确认，然后关闭窗口。
	/// 系统关闭动画时直接切换并立即关闭：登录结果不依赖动画传达。
	/// </summary>
	internal void PlaySuccess()
	{
		Paint();
		_halo.Connect();

		if (!MotionPreference.AllowAnimation) { _onDone(); return; }

		_closing?.Stop();
		_closing = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(700)};
		_closing.Tick += (_, _) => { _closing?.Stop(); _onDone(); };
		_closing.Start();
	}

	// ── 版面 ────────────────────────────────────────────────────────────────

	private Control BuildLayout()
	{
		StackPanel stage = new()
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			// 纵向居中：两种登录方式的字段数不同（密码两项、验证码一至两项），
			// 顶对齐时切换方式会导致整体位置跳动。
			VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				new Panel {Children = {_halo}, Margin = new Thickness(0, 6, 0, 20)},
				new TextBlock
				{
					Text = "登录账户",
					FontSize = 19, FontWeight = FontWeight.SemiBold,
					Foreground = ChatPalette.Primary,
					HorizontalAlignment = HorizontalAlignment.Center,
				},
				_lede,
				new StackPanel
				{
					Width = Column,
					Margin = new Thickness(0, 26, 0, 0),
					Spacing = 14,
					Children = {Block("邮箱", _email), _passwordBlock, _codeBlock},
				},
				Actions(),
			},
		};

		ScrollViewer scroll = new()
		{
			Padding = new Thickness(30, 20, 30, 24),
			Content = stage,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		};

		Control foot = Footer();
		Grid root = new() {RowDefinitions = new RowDefinitions("*,Auto")};
		root.Children.Add(scroll);
		root.Children.Add(foot);
		Grid.SetRow(scroll, 0);
		Grid.SetRow(foot, 1);
		return root;
	}

	/// <summary>一组输入。标签在上、输入在下，与首次运行各步骤一致。</summary>
	private static StackPanel Block(string label, TextBox input) => new()
	{
		Width = Column,
		Children =
		{
			new TextBlock
			{
				Text = label, FontSize = 11, Foreground = ChatPalette.Faint,
				Margin = new Thickness(2, 0, 0, 5),
			},
			input,
		},
	};

	/// <summary>
	/// 操作行。
	///
	/// 次级入口居左、主按钮居右，与首次运行底栏的排布方向一致。
	/// </summary>
	private Control Actions()
	{
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto")};
		StackPanel minor = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 14,
			VerticalAlignment = VerticalAlignment.Center,
			Children = {_switchMethod, _resend},
		};
		row.Children.Add(minor);
		row.Children.Add(_submit);
		Grid.SetColumn(minor, 0);
		Grid.SetColumn(_submit, 1);

		/*
		 * 操作行下方只保留一行文本：出错时显示错误原因，否则显示当前状态。
		 *
		 * 两者不会同时有意义 —— 出错时的「当前状态」就是「上一次提交失败」，
		 * 分成两行只会在同一位置堆叠冗余信息。
		 */
		return new StackPanel
		{
			Width = Column,
			Margin = new Thickness(0, 22, 0, 0),
			Children = {row, _line},
		};
	}

	/// <summary>
	/// 底栏：跳过登录的入口。
	///
	/// 位于分隔线之下、主区域之外，用位置表明该路径不经过上方的表单。
	/// 说明文案需明确列出失去的能力与保留的能力，避免用户误判为不登录即不可用。
	/// </summary>
	private Control Footer()
	{
		Button skip = Pill("暂不登录", primary: false);
		skip.Click += (_, _) => _onSkip();

		// 底栏只承载跳过登录的说明。此处曾同时放置状态行，导致状态文本紧邻跳过按钮，
		// 容易被理解为对该按钮的说明。
		TextBlock words = new()
		{
			Text = "不登录时，云存档与跨设备同步不可用。本地功能不受影响。",
			FontSize = 11, Foreground = ChatPalette.Faint,
			TextWrapping = TextWrapping.Wrap, MaxWidth = 230, LineHeight = 17,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto")};
		row.Children.Add(words);
		row.Children.Add(skip);
		Grid.SetColumn(words, 0);
		Grid.SetColumn(skip, 1);

		return new Border
		{
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(0, 1, 0, 0),
			Padding = new Thickness(24, 14),
			Child = row,
		};
	}

	// ── 控件 ────────────────────────────────────────────────────────────────

	/// <param name="machineText">
	/// 邮箱与验证码需要逐字符校对，而比例字体中 l/1/I 与 0/O 难以区分。
	/// 使用应用内已有的等宽字体链（工具参数显示所用），不引入新字体。
	/// </param>
	private TextBox Field(string placeholder, bool machineText = false)
	{
		TextBox box = new()
		{
			PlaceholderText = placeholder,
			Width = Column,
			FontSize = 13.5,
		};
		if (machineText) box.FontFamily = new FontFamily("Consolas, Menlo, ui-monospace, monospace");
		box.KeyDown += OnFieldKey;
		return box;
	}

	/// <summary>主/次按钮。圆角 8，内边距与首次运行的导航按钮一致。</summary>
	private static Button Pill(string text, bool primary) => new()
	{
		Content = text,
		Padding = new Thickness(primary ? 24 : 16, 8),
		CornerRadius = new CornerRadius(8),
		Background = primary ? ChatPalette.Teal : ChatPalette.Panel,
		Foreground = primary ? ChatPalette.OnTeal : ChatPalette.Body,
		BorderThickness = default,
		FontSize = 12.5,
		FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
		VerticalAlignment = VerticalAlignment.Center,
		Cursor = new Cursor(StandardCursorType.Hand),
	};

	/// <summary>纯文本的次级入口。用青色区别于主操作：它切换登录方式，不提交。</summary>
	private static Button Quiet() => new()
	{
		Background = Brushes.Transparent,
		BorderThickness = default,
		Foreground = ChatPalette.Accent,
		Padding = default,
		FontSize = 12,
		VerticalAlignment = VerticalAlignment.Center,
		Cursor = new Cursor(StandardCursorType.Hand),
	};

	// ── 行为 ────────────────────────────────────────────────────────────────

	private void OnFieldKey(object? sender, KeyEventArgs args)
	{
		if (args.Key != Key.Enter) return;
		args.Handled = true;
		Submit();
	}

	private void SwitchMethod()
	{
		bool toCode = _form.State.Method == SignInMethod.Password;
		_form.UseMethod(toCode ? SignInMethod.Code : SignInMethod.Password);
		// 切换时清空密码：用户已选择不使用密码，保留明文没有用途。
		if (toCode) { _password.Text = ""; _form.SetPassword(""); }
		Paint();
		(toCode ? _form.State.CodeSent ? _code : _email : _password).Focus();
	}

	/// <param name="force">重新发送走这条路径：主按钮此时不可用，但该操作合法。</param>
	private void Submit(bool force = false)
	{
		if (force && (_form.State.ResendSeconds > 0 || _form.State.Phase != SignInPhase.Idle)) return;
		if (!force && !_form.State.CanSubmit) return;
		if (force) _form.UseMethod(SignInMethod.Code);

		_form.BeginRequest();
		Paint();
		_onSubmit(_form.State);
	}

	private void OnSecond(object? sender, EventArgs args)
	{
		if (_form.State.ResendSeconds == 0) return;
		_form.TickResend();
		Paint();
	}

	// ── 渲染 ────────────────────────────────────────────────────────────────

	private void Paint()
	{
		SignInState state = _form.State;
		bool idle = state.Phase == SignInPhase.Idle;
		bool code = state.Method == SignInMethod.Code;
		bool bad = state.Error.Length > 0;

		_passwordBlock.IsVisible = !code;
		_codeBlock.IsVisible = code && state.CodeSent;

		_email.IsEnabled = idle;
		_password.IsEnabled = idle;
		_code.IsEnabled = idle;

		_submit.Content = state.SubmitLabel;
		_submit.IsEnabled = state.CanSubmit;
		_submit.Opacity = state.CanSubmit ? 1 : 0.4;

		// 写的是**另一条**路的名字 —— 按下去会去哪儿。
		_switchMethod.Content = code ? "使用密码登录" : "使用邮箱验证码";
		_switchMethod.IsEnabled = idle;
		_switchMethod.Opacity = idle ? 1 : 0.4;

		_resend.IsVisible = code && state.CodeSent;
		_resend.IsEnabled = idle && state.ResendSeconds == 0;
		_resend.Opacity = _resend.IsEnabled ? 1 : 0.4;
		_resend.Content = state.ResendSeconds > 0 ? $"重新发送（{state.ResendSeconds} 秒）" : "重新发送";

		/*
		 * 说明文案随登录方式变化。
		 *
		 * 验证码方式可能触发注册，密码方式不会 —— 密码只存在于已有账户。
		 */
		_lede.Text = code
			? "登录后，偏好与记忆同步至云端。首次登录将创建账户。"
			: "登录后，偏好与记忆同步至云端，可在其他设备恢复。";

		_line.Text = bad ? state.Error : state.Status;
		_line.Foreground = bad ? ChatPalette.Danger
			: state.Phase == SignInPhase.Done ? ChatPalette.Teal : ChatPalette.Faint;

		PaintHalo(state);
	}

	/// <summary>
	/// 光环档位。
	///
	/// 指示的是账户状态而非表单填写进度：条件齐备才切到就绪档，部分填写仍为空闲档。
	/// </summary>
	private void PaintHalo(SignInState state)
	{
		if (state.Phase == SignInPhase.Done) return;          // 已登录档位由 PlaySuccess 设置
		_halo.Mood = state.Phase == SignInPhase.Busy ? HaloMood.Working
			: state.CanSubmit ? HaloMood.Waking
			: HaloMood.Dormant;
	}

	// ── 测试钩子 ────────────────────────────────────────────────────────────

	/// <summary>
	/// 视觉测试用：只切到已连接档位，不启动关窗定时器。
	///
	/// 走 PlaySuccess 会在 700 毫秒后自动关闭窗口，与截图后的 Close 冲突。
	/// </summary>
	internal void ConnectForTests() { Paint(); _halo.Connect(); }

	/// <summary>视觉测试用：直接写入控件文本。只设置状态机不够，控件文本由用户输入产生。</summary>
	internal void FillForTests(string email, string password, string code)
	{
		_email.Text = email;
		_password.Text = password;
		_code.Text = code;
	}
}
