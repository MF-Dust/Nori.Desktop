using System.Globalization;
using Nori.Core.Live2D;
using Nori.Core.Security;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Diagnostics;

/// <summary>性能面板可展示的状态。缺失测量不会被当成有效的 0。</summary>
public enum Live2DPerformanceAvailability
{
	/// <summary>没有运行中的模型，或还没有可展示的测量。</summary>
	Unavailable = 0,

	/// <summary>帧采样新鲜，伴侣视窗正在渲染。</summary>
	Live = 1,

	/// <summary>曾有采样，但已超过新鲜窗口。</summary>
	Stale = 2,

	/// <summary>伴侣视窗已暂停渲染，数值是暂停前的读数。</summary>
	Paused = 3,

	/// <summary>模型加载失败。不是成功。</summary>
	LoadFailed = 4,

	/// <summary>渲染表面正在丢失或重建。</summary>
	Recovering = 5,

	/// <summary>渲染表面初始化失败，且没有安排恢复。</summary>
	RenderFailed = 6,

	/// <summary>安全模式没有启动伴侣渲染。</summary>
	SafeMode = 7,

	/// <summary>读取已有统计时失败。</summary>
	ReadFailed = 8,
}

/// <summary>
/// 从现有 <see cref="PetRuntime.RenderMetrics"/> 整理出的只读性能视图。
/// 构造过程不加载模型、不启动渲染，也不新增逐帧采样。
/// </summary>
public sealed class Live2DPerformanceSnapshot
{
	/// <summary>采样超过此时长且视窗仍标记为可见时，读数视为过期。</summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

	/// <summary>诊断字典里表示缺失测量的稳定标记。</summary>
	public const string UnavailableToken = "unavailable";

	/// <summary>尚未读取时的空视图。</summary>
	public static Live2DPerformanceSnapshot Empty { get; } = new()
	{
		Availability = Live2DPerformanceAvailability.Unavailable,
	};

	/// <summary>当前可展示状态。</summary>
	public Live2DPerformanceAvailability Availability { get; init; }

	/// <summary>渲染表面阶段。</summary>
	public PetRenderRecoveryPhase Recovery { get; init; }

	/// <summary>表面曾在故障后初始化成功。</summary>
	public bool SurfaceRestored { get; init; }

	/// <summary>存在真实的模型加载失败。</summary>
	public bool LoadFailed { get; init; }

	/// <summary>失败信息里能识别出的内置模型 ID。没有安全 ID 时为空。</summary>
	public string? FailedModelId { get; init; }

	/// <summary>安全模式阻止了自动渲染。</summary>
	public bool SafeModeBlocked { get; init; }

	/// <summary>正在显示的模型的内置 ID。未加载或不是安全 ID 时为空。</summary>
	public string? SafeModelId { get; init; }

	/// <summary>已观察到的质量档。没有运行依据时为空。</summary>
	public string? Quality { get; init; }

	/// <summary>目标帧率。没有运行依据时为空，不用 0 代替。</summary>
	public int? TargetFps { get; init; }

	/// <summary>有效渲染倍率。没有运行依据时为空。</summary>
	public float? RenderScale { get; init; }

	/// <summary>帧时间 P95。没有帧样本时为空。</summary>
	public double? FrameTimeP95Ms { get; init; }

	/// <summary>遮罩耗时 P95。没有遮罩样本时为空。</summary>
	public double? MaskCostP95Ms { get; init; }

	/// <summary>丢帧数。没有帧样本且计数为 0 时为空；真实的 0 只在有帧样本时出现。</summary>
	public int? DroppedFrames { get; init; }

	/// <summary>已发布的阴影请求。表面状态未知时为空。</summary>
	public bool? ShadowRequested { get; init; }

	/// <summary>已发布的阴影应用结果。表面状态未知时为空。</summary>
	public bool? ShadowApplied { get; init; }

	/// <summary>已发布的离屏状态。表面状态未知时为空。</summary>
	public bool? OffscreenRendering { get; init; }

	/// <summary>最近一帧样本的 UTC 时间。</summary>
	public DateTime? SampledAtUtc { get; init; }

	/// <summary>帧样本数。这是计数，不是帧时间。</summary>
	public int FrameSampleCount { get; init; }

	/// <summary>进程工作集。无法取得时为空，不用 0 代替。</summary>
	public long? ProcessWorkingSetBytes { get; init; }

	/// <summary>读取失败时的异常类型名，不含消息。</summary>
	public string? ReadErrorType { get; init; }

	/// <summary>读取进程工作集。取不到或为 0 时返回空，避免把未知内存显示成 0。</summary>
	public static long? TryWorkingSet()
	{
		try
		{
			long bytes = Environment.WorkingSet;
			return bytes > 0 ? bytes : null;
		}
		catch (PlatformNotSupportedException)
		{
			return null;
		}
	}

	/// <summary>把读取异常变成失败视图。不保存异常消息，避免带出路径或凭据。</summary>
	public static Live2DPerformanceSnapshot ReadFailed(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return new Live2DPerformanceSnapshot
		{
			Availability = Live2DPerformanceAvailability.ReadFailed,
			ReadErrorType = SensitiveDataRedactor.ExceptionType(exception),
		};
	}

	/// <summary>整理当前运行时。只读已有字段。</summary>
	public static Live2DPerformanceSnapshot Capture(PetRuntime? pet, bool safeMode, DateTime utcNow, long? workingSetBytes)
	{
		if (workingSetBytes is <= 0) workingSetBytes = null;
		if (pet is null)
		{
			return new Live2DPerformanceSnapshot
			{
				Availability = safeMode ? Live2DPerformanceAvailability.SafeMode : Live2DPerformanceAvailability.Unavailable,
				SafeModeBlocked = safeMode,
				ProcessWorkingSetBytes = workingSetBytes,
			};
		}

		PetRenderMetrics metrics = pet.RenderMetrics;
		bool modelLoaded = pet.CurrentModel is not null;
		string? error = pet.LastModelLoadError;
		bool safeMessage = string.Equals(error, PetRuntime.SafeModeModelLoadMessage, StringComparison.Ordinal);
		bool loadFailed = !string.IsNullOrWhiteSpace(error) && !safeMessage;
		Live2DPerformanceAvailability availability = ResolveAvailability(safeMode, modelLoaded, loadFailed, metrics, utcNow);
		bool hideWithoutModel = !modelLoaded;
		bool showFrames = modelLoaded
			&& metrics.FrameSampleCount > 0
			&& availability is not Live2DPerformanceAvailability.SafeMode and not Live2DPerformanceAvailability.ReadFailed;
		bool showTargets = modelLoaded
			&& availability is not Live2DPerformanceAvailability.SafeMode and not Live2DPerformanceAvailability.ReadFailed;
		bool showSurface = modelLoaded
			&& metrics.SurfaceStateKnown
			&& availability is not Live2DPerformanceAvailability.SafeMode and not Live2DPerformanceAvailability.ReadFailed;
		int? dropped = modelLoaded
			&& availability is not Live2DPerformanceAvailability.SafeMode and not Live2DPerformanceAvailability.ReadFailed
			&& (metrics.FrameSampleCount > 0 || metrics.DroppedFrames > 0)
			? metrics.DroppedFrames
			: null;
		string? quality = showTargets && metrics.EffectiveQuality is "quality" or "balanced" or "eco"
			? metrics.EffectiveQuality
			: null;

		return new Live2DPerformanceSnapshot
		{
			Availability = availability,
			Recovery = metrics.Recovery,
			SurfaceRestored = metrics.Recovery == PetRenderRecoveryPhase.Restored,
			LoadFailed = loadFailed,
			FailedModelId = loadFailed ? SafeModelToken(error) : null,
			SafeModeBlocked = availability == Live2DPerformanceAvailability.SafeMode || safeMessage,
			SafeModelId = modelLoaded ? SupportedModelIds.Normalize(pet.CurrentModelId) : null,
			Quality = quality,
			TargetFps = showTargets ? metrics.EffectiveFps : null,
			RenderScale = showTargets ? metrics.EffectiveRenderScale : null,
			FrameTimeP95Ms = showFrames ? metrics.FrameTimeP95Ms : null,
			MaskCostP95Ms = showFrames && metrics.MaskSampleCount > 0 ? metrics.MaskCostP95Ms : null,
			DroppedFrames = dropped,
			ShadowRequested = showSurface ? metrics.ShadowRequested : null,
			ShadowApplied = showSurface ? metrics.ShadowApplied : null,
			OffscreenRendering = showSurface ? metrics.OffscreenRendering : null,
			SampledAtUtc = showFrames ? metrics.LastFrameSampleUtc : null,
			FrameSampleCount = hideWithoutModel ? 0 : metrics.FrameSampleCount,
			ProcessWorkingSetBytes = workingSetBytes,
		};
	}

	/// <summary>导出用的稳定字段。缺失测量写 unavailable，不写 0。</summary>
	public Dictionary<string, string> ToDiagnosticValues()
	{
		return new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["live2d_status"] = StatusToken(Availability),
			["live2d_recovery"] = RecoveryToken(Recovery),
			["live2d_load_state"] = LoadFailed ? "load_failed" : SafeModeBlocked ? "safe_mode" : "none",
			["live2d_model_id"] = SafeModelId ?? UnavailableToken,
			["live2d_effective_quality"] = Quality ?? UnavailableToken,
			["live2d_effective_fps"] = TargetFps?.ToString(CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_render_scale"] = RenderScale?.ToString("0.##", CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_frame_time_p95_ms"] = FrameTimeP95Ms?.ToString("0.##", CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_mask_cost_p95_ms"] = MaskCostP95Ms?.ToString("0.##", CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_dropped_frames"] = DroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_shadow_requested"] = BoolToken(ShadowRequested),
			["live2d_shadow_applied"] = BoolToken(ShadowApplied),
			["live2d_offscreen"] = BoolToken(OffscreenRendering),
			["live2d_sample_time_utc"] = SampledAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_frame_sample_count"] = FrameSampleCount.ToString(CultureInfo.InvariantCulture),
			["live2d_process_working_set_bytes"] = ProcessWorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? UnavailableToken,
			["live2d_read_error"] = ReadErrorType ?? "none",
		};
	}

	/// <summary>从一段文本里找出内置模型 ID。路径或其他原文不会被当作 ID。</summary>
	internal static string? SafeModelToken(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		foreach (string id in new[] { SupportedModelIds.ArgNori, SupportedModelIds.Nori })
		{
			int index = 0;
			while ((index = text.IndexOf(id, index, StringComparison.OrdinalIgnoreCase)) >= 0)
			{
				int end = index + id.Length;
				bool left = index == 0 || !IsIdCharacter(text[index - 1]);
				bool right = end == text.Length || !IsIdCharacter(text[end]);
				if (left && right) return SupportedModelIds.Normalize(text.Substring(index, id.Length));
				index = end;
			}
		}
		return null;
	}

	private static Live2DPerformanceAvailability ResolveAvailability(
		bool safeMode,
		bool modelLoaded,
		bool loadFailed,
		PetRenderMetrics metrics,
		DateTime utcNow)
	{
		if (metrics.Recovery == PetRenderRecoveryPhase.Failed) return Live2DPerformanceAvailability.RenderFailed;
		if (metrics.Recovery is PetRenderRecoveryPhase.ContextLost or PetRenderRecoveryPhase.Rebuilding)
			return Live2DPerformanceAvailability.Recovering;
		if (loadFailed) return Live2DPerformanceAvailability.LoadFailed;
		if (!modelLoaded)
		{
			if (safeMode && metrics.FrameSampleCount == 0
				&& metrics.Recovery is PetRenderRecoveryPhase.None or PetRenderRecoveryPhase.Restored)
				return Live2DPerformanceAvailability.SafeMode;
			return Live2DPerformanceAvailability.Unavailable;
		}
		if (metrics.FrameSampleCount == 0 && !metrics.SurfaceStateKnown)
			return Live2DPerformanceAvailability.Unavailable;
		if (!metrics.Visible && metrics.SurfaceStateKnown)
			return Live2DPerformanceAvailability.Paused;
		if (metrics.FrameSampleCount > 0
			&& metrics.LastFrameSampleUtc is DateTime sampled
			&& utcNow - sampled > StaleAfter)
			return Live2DPerformanceAvailability.Stale;
		if (metrics.FrameSampleCount > 0 && metrics.Visible)
			return Live2DPerformanceAvailability.Live;
		return Live2DPerformanceAvailability.Unavailable;
	}

	private static bool IsIdCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '-';

	private static string StatusToken(Live2DPerformanceAvailability availability) => availability switch
	{
		Live2DPerformanceAvailability.Live => "live",
		Live2DPerformanceAvailability.Stale => "stale",
		Live2DPerformanceAvailability.Paused => "paused",
		Live2DPerformanceAvailability.LoadFailed => "load_failed",
		Live2DPerformanceAvailability.Recovering => "recovering",
		Live2DPerformanceAvailability.RenderFailed => "render_failed",
		Live2DPerformanceAvailability.SafeMode => "safe_mode",
		Live2DPerformanceAvailability.ReadFailed => "read_failed",
		_ => "unavailable",
	};

	private static string RecoveryToken(PetRenderRecoveryPhase phase) => phase switch
	{
		PetRenderRecoveryPhase.ContextLost => "context_lost",
		PetRenderRecoveryPhase.Rebuilding => "rebuilding",
		PetRenderRecoveryPhase.Restored => "restored",
		PetRenderRecoveryPhase.Failed => "failed",
		_ => "none",
	};

	private static string BoolToken(bool? value) => value switch
	{
		true => "true",
		false => "false",
		_ => UnavailableToken,
	};
}
