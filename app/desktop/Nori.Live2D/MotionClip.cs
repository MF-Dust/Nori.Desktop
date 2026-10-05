using System.Numerics;
using System.Text.Json;

namespace Nori.Live2D;

/// <summary>后台编译的 motion3 数据；不包含模型引用、播放游标或可变回调。</summary>
public sealed class MotionClip
{
	private MotionClip(float duration, bool loop, float fadeIn, float fadeOut, MotionCurve[] curves, MotionEvent[] events)
	{
		Duration = duration;
		SuggestedLoop = loop;
		FadeIn = fadeIn;
		FadeOut = fadeOut;
		Curves = Array.AsReadOnly(curves);
		Events = Array.AsReadOnly(events);
	}

	public float Duration { get; }
	// 编辑器循环标记不改变宿主的一次播放策略；循环由播放器显式指定。
	public bool SuggestedLoop { get; }
	public float FadeIn { get; }
	public float FadeOut { get; }
	public IReadOnlyList<MotionCurve> Curves { get; }
	public IReadOnlyList<MotionEvent> Events { get; }

	public static MotionClip Parse(ReadOnlyMemory<byte> json)
	{
		try { return ParseDocument(json); }
		catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
		{
			throw new JsonException("motion3 数据结构无效。", exception);
		}
	}

	private static MotionClip ParseDocument(ReadOnlyMemory<byte> json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		if (root.GetProperty("Version").GetInt32() != 3) throw new JsonException("不支持的 motion3 版本。");
		JsonElement meta = root.GetProperty("Meta");
		float duration = Number(meta.GetProperty("Duration"));
		if (duration <= 0) throw new JsonException("动作时长必须大于零。");
		bool restricted = meta.TryGetProperty("AreBeziersRestricted", out var restriction) && restriction.GetBoolean();
		List<MotionCurve> curves = [];
		foreach (JsonElement curve in root.GetProperty("Curves").EnumerateArray())
		{
			string target = curve.GetProperty("Target").GetString() ?? "";
			if (target is not ("Model" or "Parameter" or "PartOpacity")) throw new JsonException("动作曲线目标无效。");
			string id = curve.GetProperty("Id").GetString() ?? "";
			if (string.IsNullOrWhiteSpace(id)) throw new JsonException("动作曲线 ID 不能为空。");
			float[] data = curve.GetProperty("Segments").EnumerateArray().Select(Number).ToArray();
			curves.Add(new MotionCurve(target, id, Fade(curve, "FadeInTime", -1), Fade(curve, "FadeOutTime", -1), data, restricted, duration));
		}
		List<MotionEvent> events = [];
		if (root.TryGetProperty("UserData", out var userData))
		{
			foreach (JsonElement item in userData.EnumerateArray())
			{
				float time = Number(item.GetProperty("Time"));
				if (time < 0 || time > duration) throw new JsonException("动作事件超出时长。");
				events.Add(new MotionEvent(time, item.GetProperty("Value").GetString() ?? ""));
			}
		}
		return new MotionClip(duration, meta.TryGetProperty("Loop", out var loop) && loop.GetBoolean(),
			Fade(meta, "FadeInTime", 1), Fade(meta, "FadeOutTime", 1), curves.ToArray(), events.OrderBy(item => item.Time).ToArray());
	}

	private static float Number(JsonElement value)
	{
		if (!value.TryGetSingle(out float number) || !float.IsFinite(number)) throw new JsonException("动作数据必须为有限数字。");
		return number;
	}

	private static float Fade(JsonElement parent, string key, float fallback) =>
		parent.TryGetProperty(key, out var value) && Number(value) >= 0 ? Number(value) : fallback;
}

public readonly record struct MotionEvent(float Time, string Value);

/// <summary>线性、三次贝塞尔、阶跃、反向阶跃曲线；时间轴使用二分查找。</summary>
public sealed class MotionCurve
{
	private readonly Segment[] _segments;
	private readonly Vector2 _first;
	private readonly bool _restricted;

	internal MotionCurve(string target, string id, float fadeIn, float fadeOut, float[] data, bool restricted, float duration)
	{
		Target = target;
		Id = id;
		FadeIn = fadeIn;
		FadeOut = fadeOut;
		_restricted = restricted;
		if (data.Length < 2) throw new JsonException("动作曲线缺少起点。");
		_first = new Vector2(data[0], data[1]);
		if (_first.X < 0 || _first.X > duration) throw new JsonException("动作起点超出时长。");
		Vector2 start = _first;
		List<Segment> segments = [];
		for (int cursor = 2; cursor < data.Length;)
		{
			float kind = data[cursor++];
			if (kind is not (0 or 1 or 2 or 3)) throw new JsonException("动作插值类型无效。");
			int length = kind == 1 ? 6 : 2;
			if (data.Length - cursor < length) throw new JsonException("动作曲线段不完整。");
			Vector2 end = new(data[cursor + length - 2], data[cursor + length - 1]);
			if (end.X <= start.X || end.X > duration + 0.001f) throw new JsonException("动作曲线时间必须递增且不超出时长。");
			Vector2 a = kind == 1 ? new(data[cursor], data[cursor + 1]) : start;
			Vector2 b = kind == 1 ? new(data[cursor + 2], data[cursor + 3]) : end;
			if (a.X < start.X || a.X > end.X || b.X < start.X || b.X > end.X)
				throw new JsonException("贝塞尔控制点时间超出曲线段。");
			segments.Add(new Segment((int)kind, start, a, b, end));
			start = end;
			cursor += length;
		}
		_segments = segments.ToArray();
		// 含贝塞尔控制点：段里既有时间也有取值，这里只取值（Y）。
		MaxValue = _segments.Aggregate(_first.Y, (max, segment) =>
			Math.Max(max, Math.Max(Math.Max(segment.A.Y, segment.B.Y), segment.End.Y)));
	}

	public string Target { get; }
	public string Id { get; }
	public float FadeIn { get; }
	public float FadeOut { get; }
	/// <summary>曲线上所有点（含贝塞尔控制点）取值的最大值，解析时算好。</summary>
	public float MaxValue { get; }

	public float Evaluate(float time)
	{
		if (!float.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
		if (_segments.Length == 0 || time < _first.X) return _first.Y;
		int low = 0, high = _segments.Length;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (time < _segments[middle].End.X) high = middle;
			else low = middle + 1;
		}
		if (low == _segments.Length) return _segments[^1].End.Y;
		Segment segment = _segments[low];
		if (segment.Kind == 2) return segment.Start.Y;
		if (segment.Kind == 3) return segment.End.Y;
		float t = Math.Clamp((time - segment.Start.X) / (segment.End.X - segment.Start.X), 0, 1);
		if (segment.Kind == 0) return float.Lerp(segment.Start.Y, segment.End.Y, t);
		if (!_restricted)
		{
			// 非等距控制点须先求 x(u)=time，不能把归一化时间直接当作贝塞尔参数。
			float left = 0, right = 1;
			for (int iteration = 0; iteration < 24; iteration++)
			{
				t = (left + right) * 0.5f;
				if (Cubic(segment.Start.X, segment.A.X, segment.B.X, segment.End.X, t) < time) left = t;
				else right = t;
			}
		}
		return Cubic(segment.Start.Y, segment.A.Y, segment.B.Y, segment.End.Y, t);
	}

	private static float Cubic(float start, float a, float b, float end, float t)
	{
		float u = 1 - t;
		return u * u * u * start + 3 * u * u * t * a + 3 * u * t * t * b + t * t * t * end;
	}

	private readonly record struct Segment(int Kind, Vector2 Start, Vector2 A, Vector2 B, Vector2 End);
}
