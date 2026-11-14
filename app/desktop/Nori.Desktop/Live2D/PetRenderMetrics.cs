namespace Nori.Desktop.Live2D;

/// <summary>渲染表面故障与恢复的阶段。只描述已经发生的状态，不代表采样仍然有效。</summary>
public enum PetRenderRecoveryPhase
{
	/// <summary>尚未发生渲染表面故障。</summary>
	None = 0,

	/// <summary>OpenGL 上下文已丢失，资源已丢弃。</summary>
	ContextLost = 1,

	/// <summary>已暂停帧请求，等待或正在重建渲染表面。</summary>
	Rebuilding = 2,

	/// <summary>故障后的表面初始化已经成功。</summary>
	Restored = 3,

	/// <summary>表面初始化失败，且没有安排恢复。</summary>
	Failed = 4,
}

/// <summary>原生伴侣视窗渲染性能快照。</summary>
public sealed record PetRenderMetrics
{
	public string EffectiveQuality { get; init; } = "quality";
	public int EffectiveFps { get; init; }
	public float EffectiveRenderScale { get; init; }
	public double FrameTimeP95Ms { get; init; }
	public double MaskCostP95Ms { get; init; }
	public int DroppedFrames { get; init; }
	public bool ShadowRequested { get; init; }
	public bool ShadowApplied { get; init; }
	public bool OffscreenRendering { get; init; }
	public bool Visible { get; init; }

	/// <summary>帧时间窗口里的真实样本数。为 0 时百分位没有意义。</summary>
	public int FrameSampleCount { get; init; }

	/// <summary>遮罩耗时窗口里的真实样本数。为 0 时百分位没有意义。</summary>
	public int MaskSampleCount { get; init; }

	/// <summary>最近一次成功记入帧时间的 UTC 时刻。没有帧样本时为空。</summary>
	public DateTime? LastFrameSampleUtc { get; init; }

	/// <summary>渲染表面是否至少发布过一次状态。</summary>
	public bool SurfaceStateKnown { get; init; }

	/// <summary>最近一次渲染表面故障或恢复阶段。</summary>
	public PetRenderRecoveryPhase Recovery { get; init; }
}
