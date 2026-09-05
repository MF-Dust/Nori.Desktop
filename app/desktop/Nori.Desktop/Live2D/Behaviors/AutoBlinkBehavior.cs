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

	private readonly Random _random = new();
	private Phase _phase = Phase.Idle;
	private double _progress;
	private float _startLeft = 1.0f;
	private float _startRight = 1.0f;
	private double _delaySeconds;
	private double _openDurationSeconds;

	public AutoBlinkBehavior()
	{
		_delaySeconds = RandomRange(MinDelay, MaxDelay);
		_openDurationSeconds = RandomRange(MinBlinkOpenDuration, MaxBlinkOpenDuration);
	}

	private double RandomRange(double min, double max) => min + _random.NextDouble() * (max - min);
	private static float Clamp01(float v) => Math.Clamp(v, 0.0f, 1.0f);
	private static double EaseOutQuad(double t) => 1.0 - (1.0 - t) * (1.0 - t);
	private static double EaseInQuad(double t) => t * t;

	internal static float ApplyBlinkFactor(float baseline, float factor) => Clamp01(baseline * factor);

	private (float leftFactor, float rightFactor) UpdateBlink(double dt, float currentLeft, float currentRight)
	{
		if (_phase == Phase.Idle)
		{
			_delaySeconds = Math.Max(0, _delaySeconds - dt);
			if (_delaySeconds <= 0)
			{
				_phase = Phase.Closing;
				_progress = 0;
				_startLeft = Clamp01(currentLeft);
				_startRight = Clamp01(currentRight);
			}
			return (1.0f, 1.0f);
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
			return (factor, factor);
		}

		// Opening
		_progress = Math.Min(1.0, _progress + dt / _openDurationSeconds);
		float openEased = Clamp01((float)EaseInQuad(_progress));

		if (_progress >= 1.0)
		{
			_phase = Phase.Idle;
			_progress = 0;
			_delaySeconds = RandomRange(MinDelay, MaxDelay);
		}
		return (openEased, openEased);
	}

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.IsIdleMotion || ctx.Handled || !ctx.AutoBlinkEnabled) return;

		double safeDt = ctx.TimeDelta > 0 ? ctx.TimeDelta : 0.016;
		float currentLeft = Clamp01(ctx.Model.Model.GetParameterValue("ParamEyeLOpen"));
		float currentRight = Clamp01(ctx.Model.Model.GetParameterValue("ParamEyeROpen"));

		if (_phase == Phase.Idle && currentLeft <= 0.15f && currentRight <= 0.15f)
		{
			_progress = 0;
			_delaySeconds = RandomRange(MinDelay, MaxDelay);
			return;
		}

		bool wasActive = _phase != Phase.Idle;
		var (blinkL, blinkR) = UpdateBlink(safeDt, currentLeft, currentRight);

		if (_phase == Phase.Idle)
		{
			if (wasActive)
			{
				ctx.Model.Model.SetParameterValue("ParamEyeLOpen", ApplyBlinkFactor(_startLeft, 1.0f));
				ctx.Model.Model.SetParameterValue("ParamEyeROpen", ApplyBlinkFactor(_startRight, 1.0f));
				ctx.MarkHandled();
			}
			return;
		}

		ctx.Model.Model.SetParameterValue("ParamEyeLOpen", ApplyBlinkFactor(_startLeft, blinkL));
		ctx.Model.Model.SetParameterValue("ParamEyeROpen", ApplyBlinkFactor(_startRight, blinkR));
		ctx.MarkHandled();
	}
}
