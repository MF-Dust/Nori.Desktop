using System.Security.Cryptography;

namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 空闲眼神微动与扫视行为
///
/// 对应前端 plugins/eye-focus.ts
/// </summary>
public sealed class EyeFocusBehavior : IBehaviorPlugin
{
	private const double SaccadeStep = 400.0;
	private static readonly List<(double Prob, double Base)> SaccadeDistribution = [];

	static EyeFocusBehavior()
	{
		(double Prob, double Base)[] raw =
		[
			(0.075, 800),
			(0.110, 0),
			(0.125, 0),
			(0.140, 0),
			(0.125, 0),
			(0.050, 0),
			(0.040, 0),
			(0.030, 0),
			(0.020, 0),
			(1.000, 0),
		];

		double cumulativeProb = 0;
		double currentBase = 0;
		for (int i = 0; i < raw.Length; i++)
		{
			cumulativeProb += raw[i].Prob;
			if (i == 0) currentBase = raw[i].Base;
			else currentBase += SaccadeStep;
			SaccadeDistribution.Add((cumulativeProb, currentBase));
		}
	}

	private double _nextSaccadeAt = -1;
	private (float X, float Y) _focusTarget = (0, 0);
	private double _lastSaccadeAt = -1;

	private double RandomSaccadeInterval()
	{
		double r = NextUnit();
		foreach (var (prob, baseInterval) in SaccadeDistribution)
		{
			if (r <= prob) return baseInterval + NextUnit() * SaccadeStep;
		}
		var last = SaccadeDistribution[^1];
		return last.Base + NextUnit() * SaccadeStep;
	}

	private static double NextUnit() => RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
	private static float RandFloat(float min, float max) => min + (float)NextUnit() * (max - min);
	private static float Lerp(float a, float b, float t) => a + (b - a) * t;

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.IsIdleMotion || ctx.Handled || !ctx.ForceIdleEyeAnimation || !ctx.ModelParameters.IsBound) return;
		int eyeBallXIndex = ctx.ModelParameters.EyeBallXIndex;
		int eyeBallYIndex = ctx.ModelParameters.EyeBallYIndex;
		if (eyeBallXIndex < 0 || eyeBallYIndex < 0) return;

		var model = ctx.Model.Model;

		double now = ctx.Now;

		if (now >= _nextSaccadeAt || now < _lastSaccadeAt)
		{
			_focusTarget = (RandFloat(-1.0f, 1.0f), RandFloat(-1.0f, 0.7f));
			_lastSaccadeAt = now;
			_nextSaccadeAt = now + (RandomSaccadeInterval() / 1000.0);
		}

		float curX = model.GetParameterValue(eyeBallXIndex);
		float curY = model.GetParameterValue(eyeBallYIndex);

		model.SetParameterValue(eyeBallXIndex, Lerp(curX, _focusTarget.X, 0.3f));
		model.SetParameterValue(eyeBallYIndex, Lerp(curY, _focusTarget.Y, 0.3f));
	}
}
