namespace Nori.Core.Cloud;

/// <summary>登录方式。两者平级，随时可切。</summary>
public enum SignInMethod
{
	/// <summary>邮箱 + 密码。</summary>
	Password,

	/// <summary>邮箱 + 一次性验证码。没设过密码的人走这条。</summary>
	Code,
}

/// <summary>表单所处的阶段。</summary>
public enum SignInPhase
{
	/// <summary>可以输入。</summary>
	Idle,

	/// <summary>请求在路上，输入全部锁住。</summary>
	Busy,

	/// <summary>已登录。窗口据此播那一段收尾动画再关。</summary>
	Done,
}

/// <summary>表单的一份快照。纯数据，便于断言。</summary>
public sealed record SignInState
{
	public required SignInMethod Method { get; init; }
	public required SignInPhase Phase { get; init; }

	public required string Email { get; init; }
	public required string Password { get; init; }
	public required string Code { get; init; }

	/// <summary>验证码发出去了没有。决定验证码输入框显不显示。</summary>
	public required bool CodeSent { get; init; }

	/// <summary>还有几秒才能重发；0 表示现在就能。</summary>
	public required int ResendSeconds { get; init; }

	/// <summary>主按钮现在能不能按。</summary>
	public required bool CanSubmit { get; init; }

	/// <summary>主按钮上的字。同一个动作在整条流程里保持同一个名字。</summary>
	public required string SubmitLabel { get; init; }

	/// <summary>要显示的错误；空串表示没有。</summary>
	public required string Error { get; init; }

	/// <summary>状态行的字。</summary>
	public required string Status { get; init; }
}

/// <summary>
/// 登录表单的状态机。
///
/// 窗口只负责渲染，判断逻辑全部在此，与 <see cref="FirstRun.FirstRunWizard"/> 分工一致。
///
/// **硬约束：界面不得透露某个邮箱是否存在账户、是否设置过密码。**
/// 服务端已为此做出取舍（<c>/api/nori-auth/code</c> 不查询账户是否存在，注册关闭的判断
/// 移到验证之后），前端若实现为「输入邮箱 → 探询登录方式 → 下一步」，该探询响应即构成
/// 无需凭据的账号枚举接口，服务端的处理随之失效。
///
/// 因此两种登录方式为平级选项，由用户选择，服务端全程无需说明该账户支持哪一种。
/// 代价是未设置密码的用户可能先在密码框尝试一次；该代价可接受，换取的是无法通过本窗口
/// 枚举服务的用户。
/// </summary>
public sealed class SignInForm
{
	/// <summary>重发冷却。与服务端的 <c>NORI_OTP_RESEND_MS</c> 默认值一致。</summary>
	public const int ResendCooldownSeconds = 60;

	/// <summary>验证码位数。</summary>
	public const int CodeLength = 6;

	/// <summary>
	/// 密码最短长度。与 <c>passwords.mjs</c> 的 <c>MIN_LENGTH</c> 一致。
	///
	/// 仅用于判定提交按钮是否可用，不做强度校验：强度属于设置密码时的约束，
	/// 登录时对已有密码追加要求只会拦截合法用户。
	/// </summary>
	public const int MinPasswordLength = 12;

	private SignInMethod _method;
	private SignInPhase _phase = SignInPhase.Idle;
	private string _email = "";
	private string _password = "";
	private string _code = "";
	private bool _codeSent;
	private int _resendSeconds;
	private string _error = "";

	/// <summary>
	/// 以本机上次使用的登录方式作为初始值。
	///
	/// 默认取验证码方式：当前线上账户均未设置密码，默认展示密码输入会使全部现有用户
	/// 先经历一次失败提交，再切换到验证码方式。
	///
	/// 记录的是本机历史而非服务端状态，因此不构成账号枚举 —— 本机上次的登录方式只有
	/// 本机使用者可见。
	/// </summary>
	public SignInForm(SignInMethod initial = SignInMethod.Code) => _method = initial;

	/// <summary>当前快照。</summary>
	public SignInState State => Snapshot();

	/// <summary>
	/// 邮箱格式是否满足提交条件。
	///
	/// 比服务端的 <c>EMAIL_RE</c> 略宽即可：前端判断只决定按钮是否可用，收紧规则会把
	/// 合法但少见的地址拦在本地，而最终判据是能否收到验证邮件。
	/// </summary>
	public static bool LooksLikeEmail(string value)
	{
		string trimmed = value.Trim();
		int at = trimmed.IndexOf('@');
		if (at <= 0 || at != trimmed.LastIndexOf('@')) return false;
		string domain = trimmed[(at + 1)..];
		return domain.Length >= 3 && domain.Contains('.')
			&& !domain.StartsWith('.') && !domain.EndsWith('.')
			&& !trimmed.Any(char.IsWhiteSpace);
	}

	public void SetEmail(string value)
	{
		if (_phase == SignInPhase.Busy) return;
		string next = value.Trim();
		if (next == _email) return;
		_email = next;
		// 更换邮箱后，此前发送的验证码不对应新地址。
		_codeSent = false;
		_code = "";
		_resendSeconds = 0;
		_error = "";
	}

	public void SetPassword(string value)
	{
		if (_phase == SignInPhase.Busy) return;
		// 密码不做空白裁剪：前后空白属于密码本身，裁剪会改变用户实际设置的凭据。
		_password = value;
		_error = "";
	}

	public void SetCode(string value)
	{
		if (_phase == SignInPhase.Busy) return;
		_code = new string(value.Where(char.IsDigit).Take(CodeLength).ToArray());
		_error = "";
	}

	/// <summary>切换登录方式。已发送的验证码保留，切回时仍可使用。</summary>
	public void UseMethod(SignInMethod method)
	{
		if (_phase == SignInPhase.Busy || _method == method) return;
		_method = method;
		_error = "";
	}

	/// <summary>进入请求态，锁定输入。</summary>
	public void BeginRequest()
	{
		if (!Snapshot().CanSubmit) return;
		_phase = SignInPhase.Busy;
		_error = "";
	}

	/// <summary>
	/// 请求失败。
	///
	/// <paramref name="reason"/> 必须可直接展示：「验证失败」这类描述无法指明应修改
	/// 哪一项，用户只能重复提交相同输入。
	/// </summary>
	public void Fail(string reason)
	{
		_phase = SignInPhase.Idle;
		_error = string.IsNullOrWhiteSpace(reason) ? "服务器无响应，请稍后重试" : reason.Trim();
	}

	/// <summary>验证码已发送。开始重发冷却，并显示验证码输入。</summary>
	public void CodeDelivered()
	{
		_phase = SignInPhase.Idle;
		_method = SignInMethod.Code;
		_codeSent = true;
		_resendSeconds = ResendCooldownSeconds;
		_error = "";
	}

	/// <summary>冷却递减一秒。由窗口的定时器每秒调用。</summary>
	public void TickResend()
	{
		if (_resendSeconds > 0) _resendSeconds--;
	}

	/// <summary>登录成功。</summary>
	public void Succeed()
	{
		_phase = SignInPhase.Done;
		_error = "";
		// 成功后不再需要明文，立即清除。
		_password = "";
		_code = "";
	}

	private SignInState Snapshot()
	{
		bool emailOk = LooksLikeEmail(_email);
		bool idle = _phase == SignInPhase.Idle;

		// 主按钮在三种情形下对应不同动作，文案需说明按下后发生什么。
		(bool ready, string label) = _method switch
		{
			SignInMethod.Password => (emailOk && _password.Length >= MinPasswordLength, "登录"),
			_ when !_codeSent => (emailOk, "发送验证码"),
			_ => (emailOk && _code.Length == CodeLength, "登录"),
		};

		return new SignInState
		{
			Method = _method,
			Phase = _phase,
			Email = _email,
			Password = _password,
			Code = _code,
			CodeSent = _codeSent,
			ResendSeconds = _resendSeconds,
			CanSubmit = idle && ready,
			SubmitLabel = label,
			Error = _error,
			Status = StatusLine(emailOk),
		};
	}

	/// <summary>
	/// 状态行。
	///
	/// 描述当前状态，不用祈使句也不用语气词：这一行是给人确认「系统现在处于哪一步」，
	/// 不是提示他该做什么 —— 该做什么由字段标签和按钮文案承担。
	/// </summary>
	private string StatusLine(bool emailOk) => _phase switch
	{
		SignInPhase.Done => "已登录",
		SignInPhase.Busy => _method == SignInMethod.Code && !_codeSent ? "正在发送验证码" : "正在登录",
		_ when _error.Length > 0 => _error,
		_ when !emailOk => "邮箱未填写",
		_ when _method == SignInMethod.Password =>
			_password.Length >= MinPasswordLength ? "就绪" : "密码未填写",
		_ when !_codeSent => "就绪",
		_ when _code.Length == CodeLength => "就绪",
		_ => $"验证码已发送至 {_email}",
	};
}
