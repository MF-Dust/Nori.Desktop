using System.Globalization;
using System.Text;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Settings.Pages;

/// <summary>性能面板的一行可读文本。</summary>
public readonly record struct Live2DPerformanceRow(string Key, string Label, string Value);

/// <summary>调试页上的性能面板文案。只包含快照里已经允许展示的字段。</summary>
public sealed record Live2DPerformancePresentation(
	string Status,
	string? Detail,
	string BrushKey,
	IReadOnlyList<Live2DPerformanceRow> Rows,
	string CopyText);

/// <summary>把性能快照格式化成当前界面语言。</summary>
public static class Live2DPerformanceText
{
	/// <summary>生成状态、说明和指标行。</summary>
	public static Live2DPerformancePresentation Present(Live2DPerformanceSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		string unavailable = NativeSettingsResources.Get("perf.unavailable");
		List<string> details = [];
		if (snapshot.LoadFailed)
		{
			details.Add(snapshot.FailedModelId is { Length: > 0 } model
				? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("perf.detail.loadFailedModel"), model)
				: NativeSettingsResources.Get("perf.detail.loadFailed"));
		}
		details.Add(snapshot.Availability switch
		{
			Live2DPerformanceAvailability.Stale => NativeSettingsResources.Get("perf.detail.stale"),
			Live2DPerformanceAvailability.Paused => NativeSettingsResources.Get("perf.detail.paused"),
			Live2DPerformanceAvailability.Recovering => snapshot.Recovery == Nori.Desktop.Live2D.PetRenderRecoveryPhase.ContextLost
				? NativeSettingsResources.Get("perf.detail.lost")
				: NativeSettingsResources.Get("perf.detail.rebuilding"),
			Live2DPerformanceAvailability.RenderFailed => NativeSettingsResources.Get("perf.detail.renderFailed"),
			Live2DPerformanceAvailability.SafeMode => NativeSettingsResources.Get("perf.detail.safeMode"),
			Live2DPerformanceAvailability.ReadFailed => string.Format(
				CultureInfo.InvariantCulture,
				NativeSettingsResources.Get("perf.detail.readFailed"),
				snapshot.ReadErrorType ?? "Exception"),
			Live2DPerformanceAvailability.Unavailable when snapshot.SafeModelId is not null || snapshot.TargetFps is not null
				=> NativeSettingsResources.Get("perf.detail.noSamples"),
			Live2DPerformanceAvailability.Unavailable => NativeSettingsResources.Get("perf.detail.noModel"),
			_ => "",
		});
		if (snapshot.SurfaceRestored && snapshot.Availability is Live2DPerformanceAvailability.Live or Live2DPerformanceAvailability.Stale or Live2DPerformanceAvailability.Paused)
			details.Add(NativeSettingsResources.Get("perf.detail.restored"));
		string? detail = string.Join(" ", details.Where(item => item.Length > 0));
		if (detail.Length == 0) detail = null;

		List<Live2DPerformanceRow> rows =
		[
			Row("target_fps", "perf.targetFps", snapshot.TargetFps is int fps
				? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("perf.fpsValue"), fps.ToString(CultureInfo.InvariantCulture))
				: unavailable),
			Row("render_scale", "perf.renderScale", snapshot.RenderScale?.ToString("0.##", CultureInfo.InvariantCulture) ?? unavailable),
			Row("frame_p95", "perf.frameP95", snapshot.FrameTimeP95Ms is double frame
				? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("perf.msValue"), frame.ToString("0.##", CultureInfo.InvariantCulture))
				: unavailable),
			Row("mask_p95", "perf.maskP95", snapshot.MaskCostP95Ms is double mask
				? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("perf.msValue"), mask.ToString("0.##", CultureInfo.InvariantCulture))
				: unavailable),
			Row("dropped_frames", "perf.dropped", snapshot.DroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? unavailable),
			Row("shadow", "perf.shadow", Shadow(snapshot) ?? unavailable),
			Row("offscreen", "perf.offscreen", snapshot.OffscreenRendering switch
			{
				true => NativeSettingsResources.Get("perf.on"),
				false => NativeSettingsResources.Get("perf.off"),
				_ => unavailable,
			}),
		];
		if (snapshot.SafeModelId is { Length: > 0 } modelId)
			rows.Add(Row("model", "perf.model", modelId));
		if (snapshot.Quality is { Length: > 0 } quality)
			rows.Add(Row("quality", "perf.quality", NativeSettingsResources.Get($"perf.quality.{quality}")));
		if (snapshot.SampledAtUtc is DateTime sampled)
			rows.Add(Row("sample_time", "perf.sampleTime", sampled.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"));
		if (snapshot.ProcessWorkingSetBytes is long bytes)
			rows.Add(Row("memory", "perf.memory", FormatBytes(bytes)));

		string status = NativeSettingsResources.Get(snapshot.Availability switch
		{
			Live2DPerformanceAvailability.Live => "perf.status.live",
			Live2DPerformanceAvailability.Stale => "perf.status.stale",
			Live2DPerformanceAvailability.Paused => "perf.status.paused",
			Live2DPerformanceAvailability.LoadFailed => "perf.status.loadFailed",
			Live2DPerformanceAvailability.Recovering => "perf.status.recovering",
			Live2DPerformanceAvailability.RenderFailed => "perf.status.renderFailed",
			Live2DPerformanceAvailability.SafeMode => "perf.status.safeMode",
			Live2DPerformanceAvailability.ReadFailed => "perf.status.readFailed",
			_ => "perf.status.unavailable",
		});
		StringBuilder copy = new();
		copy.Append(status);
		if (detail is not null)
		{
			copy.Append('\n');
			copy.Append(detail);
		}
		foreach (Live2DPerformanceRow row in rows)
		{
			copy.Append('\n');
			copy.Append(row.Label);
			copy.Append(": ");
			copy.Append(row.Value);
		}
		return new Live2DPerformancePresentation(status, detail, BrushKey(snapshot.Availability), rows, copy.ToString());
	}

	private static Live2DPerformanceRow Row(string key, string labelKey, string value) =>
		new(key, NativeSettingsResources.Get(labelKey), value);

	private static string? Shadow(Live2DPerformanceSnapshot snapshot)
	{
		if (snapshot.ShadowRequested is not bool requested || snapshot.ShadowApplied is not bool applied) return null;
		if (!requested) return NativeSettingsResources.Get("perf.shadow.off");
		return NativeSettingsResources.Get(applied ? "perf.shadow.both" : "perf.shadow.pending");
	}

	private static string BrushKey(Live2DPerformanceAvailability availability) => availability switch
	{
		Live2DPerformanceAvailability.Live => "SettingsSuccessBrush",
		Live2DPerformanceAvailability.Stale or Live2DPerformanceAvailability.Paused or Live2DPerformanceAvailability.Recovering => "SettingsWarningBrush",
		Live2DPerformanceAvailability.LoadFailed or Live2DPerformanceAvailability.RenderFailed or Live2DPerformanceAvailability.ReadFailed => "SettingsErrorBrush",
		_ => "SettingsSecondaryBrush",
	};

	private static string FormatBytes(long bytes)
	{
		if (bytes >= 1L << 30) return Bytes(bytes, 1L << 30) + " GB";
		if (bytes >= 1L << 20) return Bytes(bytes, 1L << 20) + " MB";
		if (bytes >= 1L << 10) return Bytes(bytes, 1L << 10) + " KB";
		return bytes.ToString(CultureInfo.InvariantCulture) + " B";
	}

	private static string Bytes(long bytes, long unit) =>
		(bytes / (double)unit).ToString("0.0", CultureInfo.InvariantCulture);
}
