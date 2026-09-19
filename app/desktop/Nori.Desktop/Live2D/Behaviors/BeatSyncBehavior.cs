namespace Nori.Desktop.Live2D.Behaviors;

public sealed record BeatStyleConfig
{
	public required float TopYaw { get; init; }
	public required float TopRoll { get; init; }
	public required float BottomDip { get; init; }
	public float? SwingLift { get; init; }
	public required string Pattern { get; init; } // "v", "sway", "swing"
}

public sealed class BeatSyncSegment
{
	public double Start { get; set; }
	public double Duration { get; set; }
	public float FromY { get; set; }
	public float FromZ { get; set; }
	public float ToY { get; set; }
	public float ToZ { get; set; }
}

/// <summary>
/// 音乐节拍同步行为
///
/// 对应前端 plugins/beat-sync.ts（4 种节奏 + 弹簧阻尼模型）
/// </summary>
public sealed class BeatSyncBehavior : IBehaviorPlugin
{
	private static readonly Dictionary<string, BeatStyleConfig> DefaultStyles = new()
	{
		["punchy-v"] = new() { TopYaw = 6.0f, TopRoll = 8.0f, BottomDip = 5.0f, Pattern = "v" },
		["sway-sine"] = new() { TopYaw = 8.0f, TopRoll = 10.0f, BottomDip = 0.0f, SwingLift = 10.0f, Pattern = "sway" },
		["groove-step"] = new() { TopYaw = 5.0f, TopRoll = 6.0f, BottomDip = 4.0f, Pattern = "v" },
		["bounce-drop"] = new() { TopYaw = 4.0f, TopRoll = 7.0f, BottomDip = 7.0f, Pattern = "swing" },
	};

	private const float Stiffness = 120.0f;
	private const float Damping = 16.0f;
	private const float Mass = 1.0f;
	private const double ReleaseDelaySeconds = 1.8;

	private readonly List<BeatSyncSegment> _segments = [];
	private string _style = "sway-sine";
	private bool _primed;
	private bool _patternStarted;
	private string _currentTopSide = "left";
	private double? _lastBeatTimestamp;
	private float _baseY;
	private float _baseZ;

	public float TargetX { get; set; }
	public float TargetY { get; set; }
	public float TargetZ { get; set; }
	public float VelocityX { get; set; }
	public float VelocityY { get; set; }
	public float VelocityZ { get; set; }

	public void SetStyle(string style) => _style = style;
	public string GetStyle() => _style;

	private BeatStyleConfig GetStyleConfig() =>
		DefaultStyles.TryGetValue(_style, out var config) ? config : DefaultStyles["punchy-v"];

	private static float Lerp(float from, float to, float t) => from + (to - from) * t;
	private static float EaseOutCubic(float t) => 1.0f - MathF.Pow(1.0f - t, 3);

	private (float Y, float Z) GetTopPose(string side)
	{
		var cfg = GetStyleConfig();
		float direction = side == "left" ? -1.0f : 1.0f;
		float zOffset = (cfg.Pattern is "swing" or "sway") ? (cfg.SwingLift ?? cfg.TopRoll) : cfg.TopRoll;
		return (
			_baseY + direction * cfg.TopYaw,
			_baseZ + (cfg.Pattern is "swing" or "sway" ? zOffset : direction * zOffset)
		);
	}

	private (float Y, float Z) GetBottomPose()
	{
		var cfg = GetStyleConfig();
		return (_baseY, _baseZ - cfg.BottomDip);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1244", Justification = "节拍目标使用精确零值表示未初始化，近似比较会改变动画基线。")]
	public void UpdateTargets(double now)
	{
		float currentY = TargetY != 0 ? TargetY : _baseY;
		float currentZ = TargetZ != 0 ? TargetZ : _baseZ;

		while (_segments.Count > 0)
		{
			var segment = _segments[0];
			if (now < segment.Start)
			{
				currentY = segment.FromY;
				currentZ = segment.FromZ;
				break;
			}
			float progress = Math.Clamp((float)((now - segment.Start) / Math.Max(segment.Duration, 0.001)), 0.0f, 1.0f);
			float eased = EaseOutCubic(progress);
			currentY = Lerp(segment.FromY, segment.ToY, eased);
			currentZ = Lerp(segment.FromZ, segment.ToZ, eased);
			if (progress >= 1.0f)
			{
				_segments.RemoveAt(0);
				continue;
			}
			break;
		}

		double? lastBeat = _lastBeatTimestamp;
		double timeSinceBeat = _primed && lastBeat.HasValue ? (now - lastBeat.Value) : double.PositiveInfinity;
		bool shouldRelease = _primed && _segments.Count == 0 && timeSinceBeat > ReleaseDelaySeconds;

		if (shouldRelease)
		{
			_primed = false;
			_patternStarted = false;
			_currentTopSide = "left";
			_segments.Clear();
			_lastBeatTimestamp = null;
			currentY = _baseY;
			currentZ = _baseZ;
			VelocityY *= 0.5f;
			VelocityZ *= 0.5f;
		}

		TargetY = currentY;
		TargetZ = currentZ;
	}

	public void TriggerBeat(double? timestamp = null)
	{
		double nowSeconds = timestamp ?? (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
		UpdateTargets(nowSeconds);

		_baseY = TargetY;
		_baseZ = TargetZ;

		if (!_primed)
		{
			_primed = true;
			_lastBeatTimestamp = nowSeconds;
			return;
		}

		double interval = _lastBeatTimestamp.HasValue
			? Math.Clamp(nowSeconds - _lastBeatTimestamp.Value, 0.22, 2.0)
			: 0.6;
		_lastBeatTimestamp = nowSeconds;
		double halfDuration = Math.Max(0.08, interval / 2.0);

		var startPose = (Y: TargetY, Z: TargetZ);
		_segments.Clear();

		var cfg = GetStyleConfig();
		string nextSide = _currentTopSide == "left" ? "right" : "left";

		if (cfg.Pattern == "v")
		{
			if (!_patternStarted)
			{
				var topPose = GetTopPose("left");
				_segments.Add(new BeatSyncSegment
				{
					Start = nowSeconds,
					Duration = halfDuration,
					FromY = startPose.Y,
					FromZ = startPose.Z,
					ToY = topPose.Y,
					ToZ = topPose.Z,
				});
				_patternStarted = true;
				_currentTopSide = "left";
				return;
			}

			var bottomPose = GetBottomPose();
			var nextTopPose = GetTopPose(nextSide);
			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds,
				Duration = halfDuration,
				FromY = startPose.Y,
				FromZ = startPose.Z,
				ToY = bottomPose.Y,
				ToZ = bottomPose.Z,
			});
			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds + halfDuration,
				Duration = halfDuration,
				FromY = bottomPose.Y,
				FromZ = bottomPose.Z,
				ToY = nextTopPose.Y,
				ToZ = nextTopPose.Z,
			});
			_currentTopSide = nextSide;
		}
		else if (cfg.Pattern == "swing")
		{
			var sidePose = GetTopPose(_currentTopSide);
			var oppositePose = GetTopPose(nextSide);
			double sideDuration = Math.Max(0.06, interval * 0.35);
			double crossDuration = Math.Max(0.06, interval - sideDuration);

			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds,
				Duration = sideDuration,
				FromY = startPose.Y,
				FromZ = startPose.Z,
				ToY = sidePose.Y,
				ToZ = sidePose.Z,
			});
			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds + sideDuration,
				Duration = crossDuration,
				FromY = sidePose.Y,
				FromZ = sidePose.Z,
				ToY = oppositePose.Y,
				ToZ = oppositePose.Z,
			});
			_patternStarted = true;
			_currentTopSide = nextSide;
		}
		else if (cfg.Pattern == "sway")
		{
			var sidePose = GetTopPose(_currentTopSide);
			var oppositePose = GetTopPose(nextSide);
			float lift = cfg.SwingLift ?? 10.0f;

			if (!_patternStarted)
			{
				_segments.Add(new BeatSyncSegment
				{
					Start = nowSeconds,
					Duration = halfDuration,
					FromY = startPose.Y,
					FromZ = startPose.Z,
					ToY = sidePose.Y,
					ToZ = sidePose.Z,
				});
				_patternStarted = true;
				return;
			}

			double leg1 = Math.Max(0.06, interval * 0.5);
			double leg2 = Math.Max(0.06, interval - leg1);

			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds,
				Duration = leg1,
				FromY = startPose.Y,
				FromZ = startPose.Z,
				ToY = 0,
				ToZ = _baseZ + lift,
			});
			_segments.Add(new BeatSyncSegment
			{
				Start = nowSeconds + leg1,
				Duration = leg2,
				FromY = 0,
				FromZ = _baseZ + lift,
				ToY = oppositePose.Y,
				ToZ = oppositePose.Z,
			});
			_patternStarted = true;
			_currentTopSide = nextSide;
		}
	}

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.BeatSyncEnabled || !ctx.IdleAnimationEnabled) return;

		UpdateTargets(ctx.Now);
		float dt = (float)(ctx.TimeDelta > 0 ? ctx.TimeDelta : 0.016);

		float angleX = ctx.Model.Model.GetParameterValue("ParamAngleX");
		float angleY = ctx.Model.Model.GetParameterValue("ParamAngleY");
		float angleZ = ctx.Model.Model.GetParameterValue("ParamAngleZ");

		// X
		{
			float target = TargetX;
			float pos = angleX;
			float vel = VelocityX;
			float accel = (Stiffness * (target - pos) - Damping * vel) / Mass;
			VelocityX = vel + accel * dt;
			angleX = pos + VelocityX * dt;
			if (Math.Abs(target - angleX) < 0.01f && Math.Abs(VelocityX) < 0.01f)
			{
				angleX = target;
				VelocityX = 0;
			}
		}

		// Y
		{
			float target = TargetY;
			float pos = angleY;
			float vel = VelocityY;
			float accel = (Stiffness * (target - pos) - Damping * vel) / Mass;
			VelocityY = vel + accel * dt;
			angleY = pos + VelocityY * dt;
			if (Math.Abs(target - angleY) < 0.01f && Math.Abs(VelocityY) < 0.01f)
			{
				angleY = target;
				VelocityY = 0;
			}
		}

		// Z
		{
			float target = TargetZ;
			float pos = angleZ;
			float vel = VelocityZ;
			float accel = (Stiffness * (target - pos) - Damping * vel) / Mass;
			VelocityZ = vel + accel * dt;
			angleZ = pos + VelocityZ * dt;
			if (Math.Abs(target - angleZ) < 0.01f && Math.Abs(VelocityZ) < 0.01f)
			{
				angleZ = target;
				VelocityZ = 0;
			}
		}

		ctx.Model.Model.SetParameterValue("ParamAngleX", angleX);
		ctx.Model.Model.SetParameterValue("ParamAngleY", angleY);
		ctx.Model.Model.SetParameterValue("ParamAngleZ", angleZ);
	}
}
