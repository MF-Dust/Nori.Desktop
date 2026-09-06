namespace Nori.Core.Live2D;

/// <summary>渲染质量策略当前生效的目标。</summary>
public sealed record RenderQualityDecision
{
	public required Live2DQualityMode Mode { get; init; }
	public required Live2DPowerSource PowerSource { get; init; }
	public required string EffectiveQuality { get; init; }
	public required int QualityLevel { get; init; }
	public required int EffectiveFps { get; init; }
	public required float EffectiveRenderScale { get; init; }
	public required bool IsDegraded { get; init; }
}

/// <summary>
/// 原生伴侣视窗自适应质量策略。
///
/// Adaptive 在 Windows 交流电上从 60 FPS / 当前高质量倍率开始，电池上从 30 FPS / 平衡倍率开始；
/// 连续两秒超出帧预算会降一级，连续五秒稳定后升一级。Quality 与 Eco 不会被性能采样改写。
/// 显式 FPS 上限始终取策略目标与用户上限的较小值。
/// </summary>
public sealed class RenderQualityPolicy
{
	public const double DegradeAfterSeconds = 2.0;
	public const double RecoverAfterSeconds = 5.0;

	private readonly object _gate = new();
	private Live2DRenderSettings _settings;
	private Live2DPowerSource _powerSource;
	private int _qualityLevel;
	private double _overBudgetSeconds;
	private double _stableSeconds;

	public RenderQualityPolicy(Live2DRenderSettings settings, Live2DPowerSource powerSource)
	{
		_settings = settings;
		_powerSource = powerSource;
		_qualityLevel = StartingLevel(settings.QualityMode, powerSource);
	}

	/// <summary>当前输入配置。</summary>
	public Live2DRenderSettings Settings
	{
		get
		{
			lock (_gate) return _settings;
		}
	}

	/// <summary>当前质量级别与目标。</summary>
	public RenderQualityDecision Current
	{
		get
		{
			lock (_gate) return BuildDecision();
		}
	}

	/// <summary>
	/// 更新配置或电源状态。配置变化从起始级别重新开始，不沿用旧机器状态。
	/// </summary>
	public void Update(Live2DRenderSettings settings, Live2DPowerSource powerSource)
	{
		lock (_gate)
		{
			_settings = settings;
			_powerSource = powerSource;
			_qualityLevel = StartingLevel(settings.QualityMode, powerSource);
			_overBudgetSeconds = 0;
			_stableSeconds = 0;
		}
	}

	/// <summary>
	/// 记录一帧耗时；返回值表示本次是否改变了质量级别。
	/// </summary>
	public bool ObserveFrame(double frameTimeMs)
	{
		if (!double.IsFinite(frameTimeMs) || frameTimeMs <= 0) return false;

		lock (_gate)
		{
			if (_settings.QualityMode != Live2DQualityMode.Adaptive) return false;

			RenderQualityDecision current = BuildDecision();
			double budgetMs = 1000.0 / Math.Max(1, current.EffectiveFps);
			if (frameTimeMs > budgetMs)
			{
				_overBudgetSeconds += frameTimeMs / 1000.0;
				_stableSeconds = 0;
				if (_overBudgetSeconds < DegradeAfterSeconds || _qualityLevel <= 0) return false;

				_qualityLevel--;
				_overBudgetSeconds = 0;
				return true;
			}

			_stableSeconds += frameTimeMs / 1000.0;
			_overBudgetSeconds = 0;
			if (_stableSeconds < RecoverAfterSeconds || _qualityLevel >= 2) return false;

			_qualityLevel++;
			_stableSeconds = 0;
			return true;
		}
	}

	private RenderQualityDecision BuildDecision()
	{
		Live2DQualityMode mode = _settings.QualityMode;
		int level = mode switch
		{
			Live2DQualityMode.Quality => 2,
			Live2DQualityMode.Eco => 0,
			_ => _qualityLevel,
		};

		int policyFps = TargetFps(mode, _powerSource, level);
		int effectiveFps = _settings.MaxFps > 0
			? Math.Min(policyFps, _settings.MaxFps)
			: policyFps;

		float scale = mode switch
		{
			Live2DQualityMode.Quality => _settings.RenderScale,
			Live2DQualityMode.Eco => Math.Min(_settings.RenderScale, _powerSource == Live2DPowerSource.Battery ? 0.75f : 1.0f),
			_ => ScaleForAdaptive(_settings.RenderScale, _powerSource, level),
		};

		return new RenderQualityDecision
		{
			Mode = mode,
			PowerSource = _powerSource,
			EffectiveQuality = QualityName(level),
			QualityLevel = level,
			EffectiveFps = Math.Max(1, effectiveFps),
			EffectiveRenderScale = Math.Clamp(scale, Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale),
			IsDegraded = mode == Live2DQualityMode.Adaptive && level < StartingLevel(mode, _powerSource),
		};
	}

	private static int StartingLevel(Live2DQualityMode mode, Live2DPowerSource powerSource) => mode switch
	{
		Live2DQualityMode.Quality => 2,
		Live2DQualityMode.Eco => 0,
		Live2DQualityMode.Adaptive when powerSource == Live2DPowerSource.Battery => 1,
		_ => 2,
	};

	private static int TargetFps(Live2DQualityMode mode, Live2DPowerSource powerSource, int level)
	{
		if (mode == Live2DQualityMode.Quality) return powerSource == Live2DPowerSource.Battery ? 45 : 60;
		if (mode == Live2DQualityMode.Eco) return powerSource == Live2DPowerSource.Battery ? 20 : 30;

		return powerSource switch
		{
			Live2DPowerSource.Battery when level >= 1 => 30,
			Live2DPowerSource.Battery => 20,
			_ when level >= 2 => 60,
			_ when level == 1 => 45,
			_ => 30,
		};
	}

	private static float ScaleForAdaptive(float configuredScale, Live2DPowerSource powerSource, int level)
	{
		if (level >= 2) return configuredScale;
		if (level == 1) return Math.Min(configuredScale, powerSource == Live2DPowerSource.Battery ? 1.0f : 1.25f);
		return Math.Min(configuredScale, powerSource == Live2DPowerSource.Battery ? 0.65f : 0.75f);
	}

	private static string QualityName(int level) => level switch
	{
		>= 2 => "quality",
		1 => "balanced",
		_ => "eco",
	};
}
