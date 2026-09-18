using Nori.Core.FirstRun;

namespace Nori.Core.Tests;

/// <summary>
/// 首次运行向导的状态机与 AI 草稿。
///
/// 这一族对着 Vue 版 <c>wizard.ts</c> / <c>aiDraft.ts</c> 的行为写 —— 迁移成原生时
/// 最容易丢的就是这些没有画面的规则：末步不能再前进、提交失败要停在末步可重试、
/// 只选了协议不该触发一次保存。
/// </summary>
public sealed class FirstRunWizardTests
{
	private static FirstRunWizard Ok() => new(_ => Task.CompletedTask);

	private static FirstRunWizard Fails(string message) =>
		new(_ => Task.FromException(new InvalidOperationException(message)));

	[Fact]
	public void 起点是欢迎页且不能后退()
	{
		WizardState state = Ok().Snapshot();

		Assert.Equal(0, state.Index);
		Assert.Equal(WizardStep.Welcome, state.Step);
		Assert.True(state.IsFirst);
		Assert.False(state.IsLast);
		Assert.True(state.CanNext);
		Assert.False(state.CanPrev);
	}

	[Fact]
	public void 五步顺序与Vue版一致()
	{
		Assert.Equal(
			[WizardStep.Welcome, WizardStep.Language, WizardStep.Model, WizardStep.Ai, WizardStep.Ready],
			FirstRunWizard.Order);
	}

	[Fact]
	public void 走到末步就不能再前进()
	{
		FirstRunWizard wizard = Ok();
		for (int step = 0; step < FirstRunWizard.Order.Count - 1; step++) Assert.True(wizard.Next());

		WizardState state = wizard.Snapshot();
		Assert.True(state.IsLast);
		Assert.False(state.CanNext);
		Assert.False(wizard.Next());
	}

	/// <summary>方向给转场动画用：前进和后退的动画不一样。</summary>
	[Fact]
	public void 方向跟着前进后退走()
	{
		FirstRunWizard wizard = Ok();
		wizard.Next();
		Assert.Equal(1, wizard.Snapshot().Direction);
		wizard.Prev();
		Assert.Equal(-1, wizard.Snapshot().Direction);
	}

	// ── 阻断 ───────────────────────────────────────────────────────────────

	/// <summary>模型那一步没选到东西就该挡住，否则用户带着空配置进主界面。</summary>
	[Fact]
	public void 阻断之后前进不了()
	{
		FirstRunWizard wizard = Ok();
		wizard.BlockStep("请先导入一个形象");

		WizardState blocked = wizard.Snapshot();
		Assert.False(blocked.CanNext);
		Assert.Equal("请先导入一个形象", blocked.StepError);
		Assert.False(wizard.Next());

		wizard.ClearStep();
		Assert.True(wizard.Snapshot().CanNext);
		Assert.Empty(wizard.Snapshot().StepError);
		Assert.True(wizard.Next());
	}

	/// <summary>阻断是**按步**记的：在这一步被挡，不该连累别的步骤。</summary>
	[Fact]
	public void 阻断只作用于被阻断的那一步()
	{
		FirstRunWizard wizard = Ok();
		wizard.Next();                       // language
		wizard.BlockStep("这一步出错了");
		Assert.False(wizard.Next());

		wizard.Prev();                       // 回到 welcome
		Assert.True(wizard.Snapshot().CanNext);
		Assert.Empty(wizard.Snapshot().StepError);
	}

	/// <summary>后退会清掉当前步骤的错误显示，但阻断本身还在那一步上。</summary>
	[Fact]
	public void 后退清掉错误显示()
	{
		FirstRunWizard wizard = Ok();
		wizard.Next();
		wizard.BlockStep("失败");
		wizard.Prev();

		Assert.Empty(wizard.Snapshot().StepError);
		wizard.Next();
		// 回到那一步，阻断仍然生效 —— 没有人来清过它。
		Assert.False(wizard.Snapshot().CanNext);
	}

	// ── 提交 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 提交成功之后停在末步()
	{
		int calls = 0;
		FirstRunWizard wizard = new(_ =>
		{
			calls++;
			return Task.CompletedTask;
		});
		for (int step = 0; step < FirstRunWizard.Order.Count - 1; step++) wizard.Next();

		Assert.True(await wizard.FinishAsync());
		Assert.Equal(1, calls);

		WizardState state = wizard.Snapshot();
		Assert.True(state.IsLast);
		Assert.Equal(WizardFinishState.Idle, state.FinishState);
		Assert.Empty(state.FinishError);
	}

	/// <summary>失败要留在末步、把原因摆出来，并且允许再按一次。</summary>
	[Fact]
	public async Task 提交失败可以原地重试()
	{
		FirstRunWizard wizard = Fails("写配置失败");
		for (int step = 0; step < FirstRunWizard.Order.Count - 1; step++) wizard.Next();

		Assert.False(await wizard.FinishAsync());

		WizardState failed = wizard.Snapshot();
		Assert.Equal(WizardFinishState.Failed, failed.FinishState);
		Assert.Equal("写配置失败", failed.FinishError);
		Assert.True(failed.IsLast);

		// 再按一次仍然走得到回调（这里仍会失败，但不是被状态机挡住的）。
		Assert.False(await wizard.FinishAsync());
	}

	/// <summary>失败之后回头改东西，就不该还挂着上一次的错误。</summary>
	[Fact]
	public async Task 提交失败后后退清掉失败态()
	{
		FirstRunWizard wizard = Fails("写配置失败");
		for (int step = 0; step < FirstRunWizard.Order.Count - 1; step++) wizard.Next();
		await wizard.FinishAsync();

		Assert.True(wizard.Prev());
		WizardState state = wizard.Snapshot();
		Assert.Equal(WizardFinishState.Idle, state.FinishState);
		Assert.Empty(state.FinishError);
	}

	/// <summary>提交途中不许后退 —— 那会把一次正在写配置的操作丢在半路。</summary>
	[Fact]
	public async Task 提交途中不能后退()
	{
		TaskCompletionSource gate = new();
		FirstRunWizard wizard = new(_ => gate.Task);
		for (int step = 0; step < FirstRunWizard.Order.Count - 1; step++) wizard.Next();

		Task<bool> submitting = wizard.FinishAsync();
		Assert.Equal(WizardFinishState.Submitting, wizard.Snapshot().FinishState);
		Assert.False(wizard.Snapshot().CanPrev);
		Assert.False(wizard.Prev());
		// 提交中再按一次不该叠一次调用。
		Assert.False(await wizard.FinishAsync());

		gate.SetResult();
		Assert.True(await submitting);
	}

	// ── AI 草稿 ────────────────────────────────────────────────────────────

	/// <summary>只选了协议不算填过：没有密钥也没有模型，保存下去没有意义。</summary>
	[Fact]
	public void 只选协议不触发保存()
	{
		Assert.Null(AiDraftDefaults.BuildPatch(new AiDraft {Provider = AiDraftDefaults.Anthropic}));
		Assert.False(AiDraftDefaults.IsFilled(new AiDraft()));
	}

	[Theory]
	[InlineData("sk-live", "", "")]
	[InlineData("", "gpt-x", "")]
	[InlineData("", "", "https://example.test/v1")]
	public void 任意一项填了就要保存(string apiKey, string model, string baseUrl)
	{
		AiDraft draft = new() {ApiKey = apiKey, Model = model, BaseUrl = baseUrl};

		Assert.True(AiDraftDefaults.IsFilled(draft));
		Assert.NotNull(AiDraftDefaults.BuildPatch(draft));
	}

	/// <summary>
	/// 补丁里**一定要带地址**。这一步验证过的就是那个地址，不写下去会出现
	/// 「向导里能拉到模型，进主界面却连不上」。
	/// </summary>
	[Fact]
	public void 没填地址时写入该协议的默认地址()
	{
		AiChatPatch patch = Assert.IsType<AiChatPatch>(
			AiDraftDefaults.BuildPatch(new AiDraft {Provider = AiDraftDefaults.Google, ApiKey = "AIza-x"}));

		Assert.Equal("https://generativelanguage.googleapis.com/v1beta", patch.BaseUrl);
		Assert.Equal(AiDraftDefaults.Google, patch.Provider);
		Assert.Equal("AIza-x", patch.ApiKey);
		Assert.Null(patch.Model);
	}

	[Fact]
	public void 填了地址就用填的()
	{
		AiChatPatch patch = Assert.IsType<AiChatPatch>(AiDraftDefaults.BuildPatch(
			new AiDraft {BaseUrl = "  https://proxy.test/v1  ", ApiKey = "sk-x"}));

		Assert.Equal("https://proxy.test/v1", patch.BaseUrl);
	}

	/// <summary>只有空白的那几项按没填算，不能把一串空格写进配置。</summary>
	[Fact]
	public void 纯空白不算填过()
	{
		Assert.False(AiDraftDefaults.IsFilled(new AiDraft {ApiKey = "   ", Model = "\t", BaseUrl = "  "}));
	}

	[Fact]
	public void 四个协议都有默认地址和密钥提示()
	{
		foreach (string provider in AiDraftDefaults.Providers)
		{
			Assert.StartsWith("https://", AiDraftDefaults.DefaultBaseUrl(provider), StringComparison.Ordinal);
			Assert.NotEmpty(AiDraftDefaults.ApiKeyHint(provider));
		}
	}
}
