namespace Nori.Core.FirstRun;

/// <summary>向导的五步。</summary>
public enum WizardStep
{
	Welcome,
	Language,
	Model,

	/// <summary>可跳过的一步：不填也能继续，只有填了内容才在离开时落盘。</summary>
	Ai,

	Ready,
}

/// <summary>提交阶段。</summary>
public enum WizardFinishState
{
	Idle,
	Submitting,
	Failed,
}

/// <summary>向导的一份快照。纯数据，便于断言。</summary>
public sealed record WizardState
{
	public required int Index { get; init; }
	public required WizardStep Step { get; init; }

	/// <summary>这一次是前进还是后退。给转场动画用。</summary>
	public required int Direction { get; init; }

	public required bool IsFirst { get; init; }
	public required bool IsLast { get; init; }
	public required bool CanNext { get; init; }
	public required bool CanPrev { get; init; }
	public required WizardFinishState FinishState { get; init; }

	/// <summary>当前步骤的阻断原因；空串表示没被挡。</summary>
	public required string StepError { get; init; }

	/// <summary>提交失败的原因；空串表示没失败过。</summary>
	public required string FinishError { get; init; }
}

/// <summary>
/// 首次运行向导的状态机。
///
/// 步进、守卫与提交状态全收在这里，窗口只做渲染 —— 从 Vue 版
/// <c>services/firstRun/wizard.ts</c> 原样搬过来，连同它当初解决的那个问题：
/// 更早的实现里「下一步」在末步静默 no-op、模型保存与 complete_first_run 失败只往
/// 控制台打一行，界面毫无变化，观感就是「卡在选形象那一步」。所以每一步的失败都要
/// 变成**可见状态**（<see cref="WizardState.StepError"/> /
/// <see cref="WizardState.FinishError"/>）并允许重试。
///
/// 放在 Nori.Core 而不是 Nori.Desktop：它不碰 Avalonia，测试也就不必起 UI 会话。
/// </summary>
public sealed class FirstRunWizard
{
	private static readonly WizardStep[] Steps =
		[WizardStep.Welcome, WizardStep.Language, WizardStep.Model, WizardStep.Ai, WizardStep.Ready];

	/// <summary>步骤顺序。窗口按它渲染步骤指示条。</summary>
	public static IReadOnlyList<WizardStep> Order => Steps;

	private readonly Func<CancellationToken, Task> _finish;
	private readonly HashSet<WizardStep> _blocked = [];

	private int _index;
	private int _direction = 1;
	private WizardFinishState _finishState = WizardFinishState.Idle;
	private string _stepError = "";
	private string _finishError = "";

	/// <param name="finish">完成回调：成功返回，失败抛（异常消息用于展示）。</param>
	public FirstRunWizard(Func<CancellationToken, Task> finish) => _finish = finish;

	/// <summary>当前状态。</summary>
	public WizardState Snapshot() => new()
	{
		Index = _index,
		Step = Steps[_index],
		Direction = _direction,
		IsFirst = _index == 0,
		IsLast = _index == Steps.Length - 1,
		CanNext = _index < Steps.Length - 1 && !_blocked.Contains(Steps[_index]),
		CanPrev = _index > 0 && _finishState != WizardFinishState.Submitting,
		FinishState = _finishState,
		StepError = _stepError,
		FinishError = _finishError,
	};

	/// <summary>前进一步；末步或被阻断时返回 false。</summary>
	public bool Next()
	{
		if (!Snapshot().CanNext) return false;
		_direction = 1;
		_index += 1;
		_stepError = "";
		return true;
	}

	/// <summary>后退一步。顺带清掉提交失败态 —— 回去改东西就是在重试。</summary>
	public bool Prev()
	{
		if (!Snapshot().CanPrev) return false;
		_direction = -1;
		_index -= 1;
		_stepError = "";
		_finishError = "";
		if (_finishState == WizardFinishState.Failed) _finishState = WizardFinishState.Idle;
		return true;
	}

	/// <summary>标记当前步骤出错并挡住前进。</summary>
	public void BlockStep(string message)
	{
		_stepError = message;
		_blocked.Add(Steps[_index]);
	}

	/// <summary>解除当前步骤的阻断。</summary>
	public void ClearStep()
	{
		_stepError = "";
		_blocked.Remove(Steps[_index]);
	}

	/// <summary>
	/// 提交。
	///
	/// 成功后**停在末步**（窗口随后由宿主关闭）；失败回到 failed 态并把原因暴露出来，
	/// 用户可以直接再按一次。
	/// </summary>
	public async Task<bool> FinishAsync(CancellationToken cancellationToken = default)
	{
		if (_finishState == WizardFinishState.Submitting) return false;
		_finishState = WizardFinishState.Submitting;
		_finishError = "";
		try
		{
			await _finish(cancellationToken);
			_finishState = WizardFinishState.Idle;
			return true;
		}
		catch (Exception failure)
		{
			_finishState = WizardFinishState.Failed;
			_finishError = failure.Message;
			return false;
		}
	}
}
