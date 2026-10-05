using Nori.Core.Cloud;

namespace Nori.Core.Tests;

/// <summary>
/// 登录表单的契约。
///
/// 除了提交条件与状态流转，这一族覆盖两项在界面上容易遗漏且不会报错的要求：
/// 不得泄露账户是否存在，以及成功后不得继续持有明文密码。
/// </summary>
public sealed class SignInFormTests
{
	private static SignInForm Filled(string email = "a@example.com", string password = "correct horse battery")
	{
		SignInForm form = new(SignInMethod.Password);
		form.SetEmail(email);
		form.SetPassword(password);
		return form;
	}

	/// <summary>
	/// 默认走验证码。
	///
	/// 此刻线上一个密码都没有 —— 一上来摆密码框，等于让每个现有用户先撞一次墙。
	/// </summary>
	[Fact]
	public void 默认使用邮箱验证码()
	{
		SignInState state = new SignInForm().State;

		Assert.Equal(SignInMethod.Code, state.Method);
		Assert.Equal(SignInPhase.Idle, state.Phase);
		Assert.False(state.CanSubmit);
		Assert.Equal("邮箱未填写", state.Status);
	}

	/// <summary>本机上次使用密码登录时，从密码方式开始。</summary>
	[Fact]
	public void 可以指定初始登录方式()
	{
		Assert.Equal(SignInMethod.Password, new SignInForm(SignInMethod.Password).State.Method);
		Assert.Equal("登录", new SignInForm(SignInMethod.Password).State.SubmitLabel);
	}

	[Theory]
	[InlineData("a@example.com", true)]
	[InlineData("a+tag@example.com", true)]
	[InlineData("  a@example.com  ", true)]
	[InlineData("a@b.co", true)]
	[InlineData("", false)]
	[InlineData("a@example", false)]
	[InlineData("@example.com", false)]
	[InlineData("a@@example.com", false)]
	[InlineData("a b@example.com", false)]
	[InlineData("a@.com", false)]
	[InlineData("a@example.", false)]
	public void 邮箱格式校验(string value, bool ok)
	{
		SignInForm form = new();
		form.SetEmail(value);

		Assert.Equal(ok, SignInForm.LooksLikeEmail(value));
		Assert.Equal(value.Trim(), form.State.Email);
	}

	[Fact]
	public void 邮箱与密码齐备后才可提交()
	{
		SignInForm form = new(SignInMethod.Password);
		Assert.False(form.State.CanSubmit);

		form.SetEmail("a@example.com");
		Assert.False(form.State.CanSubmit);
		Assert.Equal("密码未填写", form.State.Status);

		form.SetPassword("短了点");
		Assert.False(form.State.CanSubmit);

		form.SetPassword("correct horse battery staple");
		Assert.True(form.State.CanSubmit);
		Assert.Equal("登录", form.State.SubmitLabel);
	}

	/// <summary>
	/// 密码前后的空白属于密码本身。
	///
	/// 裁剪会改变用户实际设置的凭据；密码管理器中保存的是含空白的原值，
	/// 裁剪后校验必然失败，且失败原因不可见。
	/// </summary>
	[Fact]
	public void 密码不做空白裁剪()
	{
		SignInForm form = Filled(password: "  带空格的密码短语  ");

		Assert.Equal("  带空格的密码短语  ", form.State.Password);
	}

	[Fact]
	public void 验证码只接受数字并截断至六位()
	{
		SignInForm form = new();
		form.SetCode("12a3b4c5d6e7");

		Assert.Equal("123456", form.State.Code);
	}

	[Fact]
	public void 验证码方式需先发送验证码()
	{
		SignInForm form = new();
		form.SetEmail("a@example.com");
		form.UseMethod(SignInMethod.Code);

		Assert.Equal("发送验证码", form.State.SubmitLabel);
		Assert.True(form.State.CanSubmit);
		Assert.False(form.State.CodeSent);

		form.BeginRequest();
		form.CodeDelivered();

		Assert.True(form.State.CodeSent);
		Assert.Equal("登录", form.State.SubmitLabel);
		Assert.False(form.State.CanSubmit);
		Assert.Contains("a@example.com", form.State.Status);

		form.SetCode("123456");
		Assert.True(form.State.CanSubmit);
	}

	/// <summary>发送后需切换到验证码方式，否则界面仍停留在密码输入上。</summary>
	[Fact]
	public void 发送后自动切换到验证码方式()
	{
		SignInForm form = new();
		form.SetEmail("a@example.com");
		form.UseMethod(SignInMethod.Code);
		form.BeginRequest();
		form.CodeDelivered();

		Assert.Equal(SignInMethod.Code, form.State.Method);
	}

	[Fact]
	public void 重发冷却逐秒递减至零()
	{
		SignInForm form = new();
		form.SetEmail("a@example.com");
		form.UseMethod(SignInMethod.Code);
		form.BeginRequest();
		form.CodeDelivered();

		Assert.Equal(SignInForm.ResendCooldownSeconds, form.State.ResendSeconds);
		for (int i = 0; i < SignInForm.ResendCooldownSeconds; i++) form.TickResend();
		Assert.Equal(0, form.State.ResendSeconds);

		// 归零后继续递减不应为负。
		form.TickResend();
		Assert.Equal(0, form.State.ResendSeconds);
	}

	/// <summary>更换邮箱后，此前发送的验证码不对应新地址，必须作废。</summary>
	[Fact]
	public void 更换邮箱会作废已发送的验证码()
	{
		SignInForm form = new();
		form.SetEmail("a@example.com");
		form.UseMethod(SignInMethod.Code);
		form.BeginRequest();
		form.CodeDelivered();
		form.SetCode("123456");

		form.SetEmail("b@example.com");

		Assert.False(form.State.CodeSent);
		Assert.Equal("", form.State.Code);
		Assert.Equal(0, form.State.ResendSeconds);
	}

	[Fact]
	public void 请求进行中锁定输入()
	{
		SignInForm form = Filled();
		form.BeginRequest();

		Assert.Equal(SignInPhase.Busy, form.State.Phase);
		Assert.False(form.State.CanSubmit);

		form.SetEmail("b@example.com");
		form.SetPassword("别的密码别的密码别的密码");
		form.SetCode("999999");
		form.UseMethod(SignInMethod.Code);

		Assert.Equal("a@example.com", form.State.Email);
		Assert.Equal("correct horse battery", form.State.Password);
		Assert.Equal("", form.State.Code);
		Assert.Equal(SignInMethod.Password, form.State.Method);
	}

	[Fact]
	public void 不可提交时调用不会进入请求态()
	{
		SignInForm form = new();
		form.BeginRequest();

		Assert.Equal(SignInPhase.Idle, form.State.Phase);
	}

	[Fact]
	public void 失败后解锁并保留可展示的原因()
	{
		SignInForm form = Filled();
		form.BeginRequest();
		form.Fail("密码不对");

		Assert.Equal(SignInPhase.Idle, form.State.Phase);
		Assert.Equal("密码不对", form.State.Error);
		Assert.Equal("密码不对", form.State.Status);
		Assert.True(form.State.CanSubmit);
	}

	/// <summary>未提供原因时使用默认提示，不能让状态行为空。</summary>
	[Fact]
	public void 未提供原因时使用默认提示()
	{
		SignInForm form = Filled();
		form.BeginRequest();
		form.Fail("   ");

		Assert.Equal("服务器无响应，请稍后重试", form.State.Error);
	}

	[Fact]
	public void 修改输入会清除上一次的错误()
	{
		SignInForm form = Filled();
		form.BeginRequest();
		form.Fail("密码不对");

		form.SetPassword("换一个密码试试看");
		Assert.Equal("", form.State.Error);
	}

	/// <summary>
	/// 成功后不再持有明文。
	///
	/// 窗口在登录完成后不一定立即销毁，表单对象上继续保留明文密码没有用途，
	/// 只会多出一份可被崩溃转储读取的副本。
	/// </summary>
	[Fact]
	public void 成功后不再持有明文()
	{
		SignInForm form = Filled();
		form.SetCode("123456");
		form.BeginRequest();
		form.Succeed();

		Assert.Equal(SignInPhase.Done, form.State.Phase);
		Assert.Equal("", form.State.Password);
		Assert.Equal("", form.State.Code);
		Assert.Equal("已登录", form.State.Status);
	}

	/// <summary>
	/// **界面不得透露某个邮箱是否存在账户、是否设置过密码。**
	///
	/// 服务端已为此做出取舍：/api/nori-auth/code 不查询账户是否存在。前端若实现为
	/// 「输入邮箱 → 探询登录方式 → 下一步」，该探询响应即构成无需凭据的账号枚举接口，
	/// 服务端的处理随之失效。
	///
	/// 本条约束：表单对任意邮箱给出完全相同的可用动作。
	/// </summary>
	[Fact]
	public void 对任意邮箱给出相同的可用动作()
	{
		SignInState[] snapshots =
		[
			Probe("brand-new@example.com"),
			Probe("existing-with-password@example.com"),
			Probe("existing-code-only@example.com"),
		];

		foreach (SignInState state in snapshots)
		{
			Assert.Equal(snapshots[0].Method, state.Method);
			Assert.Equal(snapshots[0].SubmitLabel, state.SubmitLabel);
			Assert.Equal(snapshots[0].CanSubmit, state.CanSubmit);
			Assert.Equal(snapshots[0].CodeSent, state.CodeSent);
			Assert.Equal(snapshots[0].Error, state.Error);
		}
		return;

		static SignInState Probe(string email)
		{
			SignInForm form = new();
			form.SetEmail(email);
			return form.State;
		}
	}
}
