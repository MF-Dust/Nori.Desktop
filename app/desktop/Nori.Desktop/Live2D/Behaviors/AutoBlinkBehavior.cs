using System.Security.Cryptography;

namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 自动眨眼行为
///
/// 对应前端 plugins/auto-blink.ts
/// - 空闲时 3~8s 间隔眨眼一次
/// - 闭眼 75ms (easeOutQuad)，睁眼 150~300ms (easeInQuad)
/// - 与当前模型参数 Multiply 混合
/// </summary>
public sealed class AutoBlinkBehavior : IBehaviorPlugin
{
	private enum Phase
	{
		Idle,
		Closing,
		Opening,
	}

	private const double BlinkCloseDuration = 0.075;
	private const double MinBlinkOpenDuration = 0.150;
	private const double MaxBlinkOpenDuration = 0.300;
	private const double MinDelay = 3.0;
	private const double MaxDelay = 8.0;

	private Phase _phase = Phase.Idle;
	private double _progress;
	private double _delaySeconds;
	private double _openDurationSeconds;

	public AutoBlinkBehavior() => Reset();

	/// <summary>取消当前眨眼并重新安排完整随机间隔；模型更换时由宿主调用。</summary>
	public void Reset()
	{
		_phase = Phase.Idle;
		_progress = 0;
		_delaySeconds = RandomRange(MinDelay, MaxDelay);
		_openDurationSeconds = RandomRange(MinBlinkOpenDuration, MaxBlinkOpenDuration);
	}

	private static double RandomRange(double min, double max) => min + NextUnit() * (max - min);
	private static double NextUnit() => RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
	private static float Clamp01(float v) => Math.Clamp(v, 0.0f, 1.0f);
	private static double EaseOutQuad(double t) => 1.0 - (1.0 - t) * (1.0 - t);
	private static double EaseInQuad(double t) => t * t;

	internal static float ApplyBlinkFactor(float baseline, float factor) => baseline * Clamp01(factor);

	internal float UpdateBlink(double dt)
	{
		if (_phase == Phase.Idle)
		{
			_delaySeconds = Math.Max(0, _delaySeconds - dt);
			if (_delaySeconds <= 0)
			{
				_phase = Phase.Closing;
				_progress = 0;
			}
			return 1.0f;
		}

		if (_phase == Phase.Closing)
		{
			_progress = Math.Min(1.0, _progress + dt / BlinkCloseDuration);
			float eased = (float)EaseOutQuad(_progress);
			float factor = Clamp01(1.0f - eased);

			if (_progress >= 1.0)
			{
				_phase = Phase.Opening;
				_progress = 0;
				_openDurationSeconds = RandomRange(MinBlinkOpenDuration, MaxBlinkOpenDuration);
			}
			return factor;
		}

		// 睁眼阶段
		_progress = Math.Min(1.0, _progress + dt / _openDurationSeconds);
		float openEased = Clamp01((float)EaseInQuad(_progress));

		if (_progress >= 1.0)
		{
			_phase = Phase.Idle;
			_progress = 0;
			_delaySeconds = RandomRange(MinDelay, MaxDelay);
		}
		return openEased;
	}

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.IsIdleMotion || ctx.Handled || !ctx.AutoBlinkEnabled || !ctx.ModelParameters.IsBound)
		{
			Reset();
			return;
		}

		int leftEyeOpenIndex = ctx.ModelParameters.LeftEyeOpenIndex;
		int rightEyeOpenIndex = ctx.ModelParameters.RightEyeOpenIndex;
		if (leftEyeOpenIndex < 0 || rightEyeOpenIndex < 0)
		{
			Reset();
			return;
		}

		var model = ctx.Model.Model;

		double safeDt = ctx.TimeDelta > 0 ? ctx.TimeDelta : 0.016;
		float currentLeft = model.GetParameterValue(leftEyeOpenIndex);
		float currentRight = model.GetParameterValue(rightEyeOpenIndex);

		if (_phase == Phase.Idle && currentLeft <= 0.15f && currentRight <= 0.15f)
		{
			_progress = 0;
			_delaySeconds = RandomRange(MinDelay, MaxDelay);
			return;
		}

		float blinkFactor = UpdateBlink(safeDt);
		// 结束当帧不再写眼值，保留当前动作与表情的结果。
		if (_phase == Phase.Idle) return;

		model.SetParameterValue(leftEyeOpenIndex, ApplyBlinkFactor(currentLeft, blinkFactor));
		model.SetParameterValue(rightEyeOpenIndex, ApplyBlinkFactor(currentRight, blinkFactor));
		ctx.MarkHandled();
	}
}
