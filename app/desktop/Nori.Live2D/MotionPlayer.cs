namespace Nori.Live2D;

public enum MotionPriority { None, Idle, Normal, Force }

/// <summary>一次播放的独立状态；同一动作在桌宠与预览中播放不会共享游标或回调。</summary>
public sealed class MotionPlayback
{
	internal MotionPlayback(MotionClip clip, MotionPriority priority, float fadeIn, float fadeOut, bool loop, Action? finished)
	{
		Clip = clip;
		Priority = priority;
		FadeIn = fadeIn;
		FadeOut = fadeOut;
		Loop = loop;
		Finished = finished;
	}

	public MotionClip Clip { get; }
	public MotionPriority Priority { get; }
	public bool IsFinished { get; internal set; }
	public double Elapsed { get; internal set; }
	internal float FadeIn { get; }
	internal float FadeOut { get; }
	internal bool Loop { get; }
	internal Action? Finished { get; }
	internal bool Started { get; set; }
	internal double? InterruptedAt { get; set; }
}

/// <summary>单模型动作调度器，负责优先级、交叉淡化、效果通道和事件；调用方串行使用。</summary>
public sealed class MotionPlayer
{
	private readonly NativeModel _model;
	private readonly HashSet<int> _eyes;
	private readonly HashSet<int> _lips;
	private readonly Dictionary<MotionClip, BoundClip> _bindings = [];
	private readonly List<MotionPlayback> _playing = [];

	public MotionPlayer(NativeModel model, IEnumerable<string> eyeBlinkIds, IEnumerable<string> lipSyncIds)
	{
		_model = model;
		_eyes = eyeBlinkIds.Select(model.GetParameterIndex).ToHashSet();
		_lips = lipSyncIds.Select(model.GetParameterIndex).ToHashSet();
	}

	public bool IsFinished => _playing.Count == 0;
	public MotionPriority CurrentPriority => _playing.Count == 0 ? MotionPriority.None : _playing[^1].Priority;
	public event Action<string>? EventFired;

	public MotionPlayback? Start(MotionClip clip, MotionPriority priority, Action? finished = null,
		float? fadeIn = null, float? fadeOut = null, bool loop = false)
	{
		ArgumentNullException.ThrowIfNull(clip);
		float incomingFade = fadeIn ?? clip.FadeIn, outgoingFade = fadeOut ?? clip.FadeOut;
		if (!float.IsFinite(incomingFade) || incomingFade < 0 || !float.IsFinite(outgoingFade) || outgoingFade < 0)
			throw new ArgumentOutOfRangeException(nameof(fadeIn), "动作淡化时间必须为非负有限值。");
		MotionPriority current = CurrentPriority;
		if (priority != MotionPriority.Force && priority <= current) return null;
		if (!_bindings.ContainsKey(clip))
		{
			var coveredParameters = new HashSet<int>();
			BoundCurve[] curves = new BoundCurve[clip.Curves.Count];
			for (int i = 0; i < curves.Length; i++)
			{
				MotionCurve curve = clip.Curves[i];
				CurveTarget target = curve.Target switch
				{
					"Model" => CurveTarget.Model,
					"Parameter" => CurveTarget.Parameter,
					"PartOpacity" => CurveTarget.PartOpacity,
					_ => CurveTarget.Other,
				};
				CurveEffect effect = target == CurveTarget.Model ? curve.Id switch
				{
					"EyeBlink" => CurveEffect.EyeBlink,
					"LipSync" => CurveEffect.LipSync,
					"Opacity" => CurveEffect.Opacity,
					_ => CurveEffect.Other,
				} : CurveEffect.Other;
				int index = target switch
				{
					CurveTarget.Parameter => _model.GetParameterIndex(curve.Id),
					CurveTarget.PartOpacity => _model.GetPartIndex(curve.Id),
					_ => -1,
				};
				bool isEye = target == CurveTarget.Parameter && _eyes.Contains(index);
				bool isLip = target == CurveTarget.Parameter && _lips.Contains(index);
				if (target == CurveTarget.Parameter) coveredParameters.Add(index);
				curves[i] = new BoundCurve(curve, index, target, effect, isEye, isLip);
			}
			_bindings.Add(clip, new BoundClip(curves, Untracked(_eyes, coveredParameters), Untracked(_lips, coveredParameters)));
		}
		foreach (MotionPlayback previous in _playing) previous.InterruptedAt ??= previous.Elapsed;
		var playback = new MotionPlayback(clip, priority, incomingFade, outgoingFade, loop, finished);
		_playing.Add(playback);
		return playback;
	}

	public void Stop()
	{
		foreach (MotionPlayback playback in _playing) playback.IsFinished = true;
		_playing.Clear();
	}

	public void Update(float deltaSeconds)
	{
		if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
		List<Action>? callbacks = null;
		foreach (MotionPlayback playback in _playing)
		{
			double before = playback.Started ? playback.Elapsed : -double.Epsilon;
			if (playback.Started) playback.Elapsed += deltaSeconds;
			playback.Started = true;
			double end = playback.Loop ? double.PositiveInfinity : playback.Clip.Duration;
			if (playback.InterruptedAt is { } interrupted) end = Math.Min(end, interrupted + playback.FadeOut);
			double elapsed = Math.Min(playback.Elapsed, end);
			float time = (float)(playback.Loop ? elapsed % playback.Clip.Duration : elapsed);
			Apply(playback, time, end);

			if (EventFired is { } handler)
			{
				// 按跨过的时间区间派发，长帧跨越循环边界也不漏掉事件。
				long firstCycle = playback.Loop ? (long)(Math.Max(0, before) / playback.Clip.Duration) : 0;
				long lastCycle = playback.Loop ? (long)(elapsed / playback.Clip.Duration) : 0;
				for (long cycle = firstCycle; cycle <= lastCycle; cycle++)
				{
					foreach (MotionEvent item in playback.Clip.Events)
					{
						double at = cycle * (double)playback.Clip.Duration + item.Time;
						if (at > before && at <= elapsed) QueueEvent(ref callbacks, handler, item.Value);
					}
				}
			}
			if (playback.Elapsed >= end)
			{
				playback.IsFinished = true;
				if (playback.InterruptedAt is null && playback.Finished is { } finished) (callbacks ??= []).Add(finished);
			}
		}
		for (int i = _playing.Count - 1; i >= 0; i--)
			if (_playing[i].IsFinished) _playing.RemoveAt(i);
		// 先完成本帧的队列变更，再让宿主回调启动或停止动作。
		if (callbacks is not null) foreach (Action callback in callbacks) callback();
	}

	private static void QueueEvent(ref List<Action>? callbacks, Action<string> handler, string value) =>
		(callbacks ??= []).Add(() => handler(value));

	private void Apply(MotionPlayback playback, float time, double end)
	{
		BoundClip binding = _bindings[playback.Clip];
		BoundCurve[] curves = binding.Curves;
		float? blink = null, lip = null;
		foreach (BoundCurve bound in curves)
		{
			if (bound.Target != CurveTarget.Model) continue;
			float value = bound.Curve.Evaluate(time);
			switch (bound.Effect)
			{
				case CurveEffect.EyeBlink: blink = value; break;
				case CurveEffect.LipSync: lip = value; break;
				case CurveEffect.Opacity: _model.Opacity = value; break;
			}
		}
		foreach (BoundCurve bound in curves)
		{
			MotionCurve curve = bound.Curve;
			if (bound.Target == CurveTarget.Model) continue;
			float weight = Weight(playback, end, curve.FadeIn, curve.FadeOut);
			float value = curve.Evaluate(time);
			if (bound.Target == CurveTarget.PartOpacity)
				_model.SetPartOpacity(bound.Index, float.Lerp(_model.GetPartOpacity(bound.Index), value, weight));
			else
			{
				if (blink is { } eyeValue && bound.IsEye) value *= eyeValue;
				if (lip is { } lipValue && bound.IsLip) value += lipValue;
				_model.SetParameterValue(bound.Index, value, weight);
			}
		}
		float effectWeight = Weight(playback, end, -1, -1);
		if (blink is { } eyeEffect) ApplyUntrackedEffect(binding.UntrackedEyes, eyeEffect, effectWeight);
		if (lip is { } lipEffect) ApplyUntrackedEffect(binding.UntrackedLips, lipEffect, effectWeight);
	}

	private static int[] Untracked(HashSet<int> ids, HashSet<int> covered)
	{
		var result = new List<int>();
		foreach (int index in ids)
			if (!covered.Contains(index)) result.Add(index);
		return result.ToArray();
	}

	private void ApplyUntrackedEffect(int[] indices, float value, float weight)
	{
		foreach (int index in indices) _model.SetParameterValue(index, value, weight);
	}

	internal static float Ease(double progress) => (float)((1 - Math.Cos(Math.Clamp(progress, 0, 1) * Math.PI)) * 0.5);

	private static float Weight(MotionPlayback playback, double end, float curveIn, float curveOut)
	{
		float fadeIn = curveIn < 0 ? playback.FadeIn : curveIn;
		float fadeOut = curveOut < 0 ? playback.FadeOut : curveOut;
		float incoming = fadeIn == 0 ? 1 : Ease(playback.Elapsed / fadeIn);
		float outgoing = fadeOut == 0 || double.IsPositiveInfinity(end) ? 1 : Ease((end - playback.Elapsed) / fadeOut);
		return incoming * outgoing;
	}

	private enum CurveTarget { Other, Model, Parameter, PartOpacity }
	private enum CurveEffect { Other, EyeBlink, LipSync, Opacity }
	private sealed record BoundClip(BoundCurve[] Curves, int[] UntrackedEyes, int[] UntrackedLips);
	private readonly record struct BoundCurve(MotionCurve Curve, int Index, CurveTarget Target, CurveEffect Effect, bool IsEye, bool IsLip);
}
