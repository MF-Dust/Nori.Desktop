namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 口型同步行为
///
/// 对应前端 plugins/lip-sync.ts
/// - 说话时直接覆盖 ParamMouthOpenY
/// - 停止说话后 200ms 平滑释放 (smoothstep) + 500ms 闭口保持
/// </summary>
public sealed class LipSyncBehavior : IBehaviorPlugin
{
	private const double ReleaseDurationSeconds = 0.200;
	private const double HandoffHoldSeconds = 0.500;

	private float _mouthOpenSize;
	private bool _nowSpeaking;
	private double _releaseRemainingSeconds;
	private double _handoffRemainingSeconds;
	private float _lastForcedValue;

	public void SetMouthOpen(float value) => _mouthOpenSize = Math.Clamp(value, 0.0f, 1.0f);
	public void SetNowSpeaking(bool speaking) => _nowSpeaking = speaking;

	private static float Smoothstep(float t) => t * t * (3.0f - 2.0f * t);

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.LipSyncEnabled || !ctx.ModelParameters.IsBound) return;
		int mouthOpenIndex = ctx.ModelParameters.MouthOpenIndex;
		if (mouthOpenIndex < 0) return;

		var model = ctx.Model.Model;

		if (_nowSpeaking)
		{
			_lastForcedValue = _mouthOpenSize;
			_releaseRemainingSeconds = ReleaseDurationSeconds;
			_handoffRemainingSeconds = HandoffHoldSeconds;
			model.SetParameterValue(mouthOpenIndex, _mouthOpenSize);
			return;
		}

		if (_releaseRemainingSeconds <= 0)
		{
			if (_handoffRemainingSeconds > 0)
			{
				_handoffRemainingSeconds = Math.Max(0, _handoffRemainingSeconds - ctx.TimeDelta);
				model.SetParameterValue(mouthOpenIndex, 0);
			}
			return;
		}

		_releaseRemainingSeconds = Math.Max(0, _releaseRemainingSeconds - ctx.TimeDelta);
		float blend = Smoothstep(Math.Clamp((float)(1.0 - _releaseRemainingSeconds / ReleaseDurationSeconds), 0.0f, 1.0f));

		float motionValue = model.GetParameterValue(mouthOpenIndex);
		float blended = _lastForcedValue * (1.0f - blend) + motionValue * blend;

		model.SetParameterValue(mouthOpenIndex, blended);
	}
}
