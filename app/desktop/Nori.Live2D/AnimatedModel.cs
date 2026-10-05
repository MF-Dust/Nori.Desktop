using System.Numerics;

namespace Nori.Live2D;

/// <summary>模型与动画的独占运行实例；仅接收已准备的数据，不访问文件或图形上下文。</summary>
public sealed class AnimatedModel : IDisposable
{
	private const string IdleGroup = "Idle";

	private readonly Dictionary<string, BoundMotion[]> _groups = new(StringComparer.Ordinal);
	private readonly MotionPlayer _motions;
	private readonly BreathPlayer _breath;
	private readonly PhysicsPlayer? _physics;
	private readonly PosePlayer? _pose;
	private bool _disposed;

	public NativeModel Model { get; }
	public ModelDefinition Definition { get; }
	public bool RandomMotion { get; set; } = true;
	public bool IsMotionFinished => _motions.IsFinished;
	public string? CurrentMotionGroup { get; private set; }
	public event Action<string>? MotionEvent;
	public Action? BeforeEffects { get; set; }
	public Action? AfterEffects { get; set; }

	public AnimatedModel(ReadOnlySpan<byte> mocBytes, ModelDefinition definition,
		IReadOnlyDictionary<string, MotionClip> motions, PhysicsDefinition? physics = null, PoseDefinition? pose = null)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(motions);
		Definition = definition;
		// 在创建原生模型之前确认动作资源完整；不把 GL 提交变成文件加载回退路径。
		foreach (var group in definition.Motions)
		{
			BoundMotion[] entries = new BoundMotion[group.Value.Count];
			for (int i = 0; i < entries.Length; i++)
			{
				if (!motions.TryGetValue($"{group.Key}_{i}", out MotionClip? clip) || clip is null)
					throw new InvalidOperationException($"缺少预加载动作: {group.Key}_{i}");
				// 加载时判一次就够：曲线不会变，而待机每隔几秒就要挑一次。
				entries[i] = new(clip, group.Value[i], MotionEyeState.KeepsEyesClosed(clip));
			}
			_groups.Add(group.Key, entries);
		}
		Model = new NativeModel(mocBytes);
		try
		{
			_motions = new MotionPlayer(Model, definition.EyeBlinkIds, definition.LipSyncIds);
			_motions.EventFired += value => MotionEvent?.Invoke(value);
			_breath = new BreathPlayer(Model,
				("ParamAngleX", 0, 15, 6.5345f, 0.5f),
				("ParamAngleY", 0, 8, 3.5345f, 0.5f),
				("ParamAngleZ", 0, 10, 5.5345f, 0.5f),
				("ParamBodyAngleX", 0, 4, 15.5345f, 0.5f),
				("ParamBreath", 0.5f, 0.5f, 3.2345f, 0.5f));
			if (physics is not null) _physics = new PhysicsPlayer(Model, physics);
			if (pose is not null) _pose = new PosePlayer(Model, pose);
		}
		catch
		{
			Model.Dispose();
			throw;
		}
	}

	public void Update(float deltaSeconds)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
			throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "模型时间增量必须是有限非负数");
		// 快照只保存动作结果，宿主行为、呼吸及物理不累积进下一帧的基准。
		Model.LoadParameters();
		if (RandomMotion && _motions.IsFinished) StartRandomMotion(IdleGroup, MotionPriority.Idle);
		else _motions.Update(deltaSeconds);
		Model.SaveParameters();
		BeforeEffects?.Invoke();
		_breath.Update(deltaSeconds);
		_physics?.Update(deltaSeconds);
		_pose?.Update(deltaSeconds);
		AfterEffects?.Invoke();
		Model.Update();
	}

	public MotionPlayback? StartMotion(string group, int index, MotionPriority priority, Action? finished = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		string? key = ResolveGroup(group);
		if (key is null || (uint)index >= (uint)_groups[key].Length) return null;
		BoundMotion motion = _groups[key][index];
		MotionPlayback? playback = _motions.Start(motion.Clip, priority, finished,
			motion.Reference.FadeInTime >= 0 ? motion.Reference.FadeInTime : null,
			motion.Reference.FadeOutTime >= 0 ? motion.Reference.FadeOutTime : null);
		if (playback is not null) CurrentMotionGroup = key;
		return playback;
	}

	public MotionPlayback? StartRandomMotion(string group, MotionPriority priority, Action? finished = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		string? key = ResolveGroup(group);
		if (key is null || _groups[key].Length == 0) return null;
		BoundMotion[] entries = _groups[key];
		// 待机组避开全程闭眼的睡觉动作，见 MotionEyeState。
		int index = key.Equals(IdleGroup, StringComparison.OrdinalIgnoreCase)
			? MotionEyeState.PickIndex([.. entries.Select(entry => entry.EyesClosed)], Random.Shared)
			: Random.Shared.Next(entries.Length);
		return StartMotion(key, index, priority, finished);
	}

	public void StopAllMotions()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_motions.Stop();
		CurrentMotionGroup = null;
	}

	/// <summary>模型坐标下的命名绘制区域矩形命中；空网格与不存在的区域不命中。</summary>
	public unsafe bool HitTest(string areaName, Vector2 point)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (Model.Opacity < 1 || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
		ModelHitArea? area = Definition.HitAreas.FirstOrDefault(area => area.Name.Equals(areaName, StringComparison.OrdinalIgnoreCase));
		int index = area is null ? -1 : Model.GetDrawableIndex(area.Id);
		if (index < 0) return false;
		int count = Model.GetDrawableVertexCount(index);
		Vector2* vertices = Model.GetDrawableVertexPositions(index);
		Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
		for (int i = 0; i < count; i++)
		{
			min = Vector2.Min(min, vertices[i]);
			max = Vector2.Max(max, vertices[i]);
		}
		GC.KeepAlive(Model);
		return point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_motions.Stop();
		BeforeEffects = null;
		AfterEffects = null;
		MotionEvent = null;
		Model.Dispose();
	}

	private string? ResolveGroup(string group) => string.IsNullOrWhiteSpace(group) ? null
		: _groups.ContainsKey(group) ? group
		: _groups.Keys.FirstOrDefault(key => key.Equals(group, StringComparison.OrdinalIgnoreCase));

	private sealed record BoundMotion(MotionClip Clip, ModelMotion Reference, bool EyesClosed);
}
