namespace Nori.Desktop.Live2D;

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
}
