namespace Nori.Core.Emotion;

/// <summary>
/// 情绪类型 (8 种基础情绪)
/// </summary>
public static class EmotionTypes
{
	public const string Neutral = "neutral";
	public const string Happy = "happy";
	public const string Sad = "sad";
	public const string Angry = "angry";
	public const string Surprised = "surprised";
	public const string Shy = "shy";
	public const string Sleepy = "sleepy";
	public const string Fond = "fond";

	/// <summary>全部合法情绪值</summary>
	public static readonly IReadOnlyList<string> All =
	[Neutral, Happy, Sad, Angry, Surprised, Shy, Sleepy, Fond];

	public static bool IsValid(string value) => All.Contains(value);
}

/// <summary>
/// 情绪状态描述
/// </summary>
public sealed record EmotionState
{
	/// <summary>情绪类型</summary>
	public required string Type { get; init; }

	/// <summary>强度 0.0 ~ 1.0</summary>
	public required double Intensity { get; init; }

	/// <summary>最后更新时间戳</summary>
	public required long LastUpdated { get; init; }
}

/// <summary>
/// 情绪状态管理器
///
/// 支持配置持久化与自然衰减: 每 DecayIntervalSeconds 秒衰减 0.1, 归零后回到 neutral。
/// 情绪变化时通过 ExpressionRequested 请求 Live2D 默认表情映射。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2931", Justification = "计时器已在 Dispose 中释放，属于分析器误报。")]
public sealed class EmotionManager(Nori.Core.Configuration.ConfigStore configStore) : IDisposable
{
	/// <summary>自然衰减周期 (秒), 与前端实现一致</summary>
	public const int DecayIntervalSeconds = 20;

	private readonly object _gate = new();
	private System.Threading.Timer? _decayTimer;

	private string _current = EmotionTypes.Neutral;
	private double _intensity = 0.5;
	private long _lastUpdated;
	private bool _initialized;
	private System.Threading.Timer? _persistTimer;

	/// <summary>情绪变化通知</summary>
	public event Action<EmotionState>? Changed;

	/// <summary>情绪到默认 Live2D 表情的映射请求</summary>
	public event Action<string>? ExpressionRequested;

	/// <summary>从配置恢复持久化的情绪状态</summary>
	public void Initialize()
	{
		lock (_gate)
		{
			if (_initialized) return;
			string savedType = configStore.GetStringOr("nori_emotion", "");
			double savedIntensity = ParseDouble(configStore.GetStringOr("nori_emotion_intensity", ""), double.NaN);
			if (savedType.Length > 0 && EmotionTypes.IsValid(savedType))
			{
				_current = savedType;
			}
			if (!double.IsNaN(savedIntensity) && savedIntensity is >= 0 and <= 1)
			{
				_intensity = savedIntensity;
			}
			_lastUpdated = Environment.TickCount64;
			_initialized = true;
		}
		StartDecayLoop();
	}

	/// <summary>获取当前情绪状态</summary>
	public EmotionState GetState()
	{
		lock (_gate)
		{
			return new EmotionState {Type = _current, Intensity = _intensity, LastUpdated = _lastUpdated};
		}
	}

	/// <summary>当前情绪类型 (供 Prompt 注入)</summary>
	public string CurrentType => GetState().Type;

	/// <summary>更新情绪状态并持久化 (防抖 400ms)</summary>
	public void SetEmotion(string type, double intensity = 0.8)
	{
		if (!EmotionTypes.IsValid(type)) throw new InvalidOperationException($"未知的情绪类型: {type}");
		long now = Environment.TickCount64;
		lock (_gate)
		{
			_current = type;
			_intensity = Math.Clamp(intensity, 0, 1);
			_lastUpdated = now;
		}
		Changed?.Invoke(GetState());
		RequestExpression(type);
		SchedulePersist();
	}

	/// <summary>映射情绪到默认 Live2D 表情</summary>
	private void RequestExpression(string emotion)
	{
		string expression = emotion switch
		{
			EmotionTypes.Happy => "Smile",
			EmotionTypes.Sad => "Sad",
			EmotionTypes.Angry => "Angry",
			EmotionTypes.Surprised => "Surprised",
			EmotionTypes.Shy => "Shy",
			EmotionTypes.Sleepy => "Sleepy",
			EmotionTypes.Fond => "Smile",
			_ => "",
		};
		if (expression.Length > 0) ExpressionRequested?.Invoke(expression);
	}

	/// <summary>防抖保存情绪状态到数据库 (400ms)</summary>
	private void SchedulePersist()
	{
		lock (_gate)
		{
			// 每次重置独立 timer, 只保留最新一个 (与前端每字段独立 timer 同理)
			_persistTimer?.Dispose();
			_persistTimer = new System.Threading.Timer(_ =>
			{
				try
				{
					EmotionState state = GetState();
					configStore.Set("nori_emotion", new Configuration.ConfigValue.Text(state.Type));
					configStore.Set("nori_emotion_intensity", new Configuration.ConfigValue.Text(
						state.Intensity.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture)));
				}
				catch
				{
					// 持久化失败只影响下次启动的情绪恢复
				}
			}, null, 400, Timeout.Infinite);
		}
	}

	private void StartDecayLoop()
	{
		lock (_gate)
		{
			_decayTimer ??= new System.Threading.Timer(_ =>
			{
				bool changed = false;
				lock (_gate)
				{
					if (_current == EmotionTypes.Neutral) return;
					_intensity -= 0.1;
					if (_intensity <= 0.1)
					{
						_current = EmotionTypes.Neutral;
						_intensity = 0.5;
					}
					changed = true;
				}
				if (changed)
				{
					Changed?.Invoke(GetState());
					SchedulePersist();
				}
			}, null, DecayIntervalSeconds * 1000, DecayIntervalSeconds * 1000);
		}
	}

	/// <summary>测试辅助: 手动推进一次衰减 (20s 周期的确定性替代)</summary>
	public void TickDecayForTests()
	{
		bool changed;
		lock (_gate)
		{
			if (_current == EmotionTypes.Neutral) return;
			_intensity -= 0.1;
			if (_intensity <= 0.1)
			{
				_current = EmotionTypes.Neutral;
				_intensity = 0.5;
			}
			changed = true;
		}
		if (changed) Changed?.Invoke(GetState());
	}

	private static double ParseDouble(string raw, double fallback) =>
		double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
			? value
			: fallback;

	public void Dispose()
	{
		lock (_gate)
		{
			_decayTimer?.Dispose();
			_decayTimer = null;
			_persistTimer?.Dispose();
			_persistTimer = null;
		}
	}
}
