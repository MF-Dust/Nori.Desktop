using System.Runtime.InteropServices;
using Nori.Core.Data;
using Nori.Core.Security;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Diagnostics;

/// <summary>
/// 运行诊断信息构建器
///
/// 迷你版 ClassIsland DiagnosticService.GetDiagnosticInfo:
/// 汇总调试页展示与问题反馈所需的静态环境信息, 全部为即时读取, 无需持有状态.
/// </summary>
public static class DiagnosticInfo
{
	/// <summary>
	/// 构建诊断信息字典 (键为英文 snake_case 便于检索, 值为可读文本)
	/// </summary>
	/// <summary>构建诊断信息并附带当前伴侣渲染指标。</summary>
	public static Dictionary<string, string> Build(PetRuntime? pet, AppStoragePaths paths, bool safeMode = false)
	{
		string version = Nori.Core.ProductVersion.Current;
		PetRenderMetrics? render = pet?.RenderMetrics;
		ArgumentNullException.ThrowIfNull(paths);
		AppStoragePaths storage = paths;
		long dbBytes = FileSizeOrDefault(storage.DatabasePath);
		return new Dictionary<string, string>
		{
			["app_version"] = version,
			["safe_mode"] = safeMode.ToString().ToLowerInvariant(),
			["dotnet_version"] = Environment.Version.ToString(),
			["os_version"] = RuntimeInformation.OSDescription,
			["os_arch"] = RuntimeInformation.OSArchitecture.ToString(),
			["process_uptime"] = FormatDuration(Environment.TickCount64),
			["process_bits"] = Environment.Is64BitProcess ? "64-bit" : "32-bit",
			["data_dir"] = SensitiveDataRedactor.Redact(storage.DataRoot),
			["resources_dir"] = SensitiveDataRedactor.Redact(storage.ResourcesDirectory),
			["log_dir"] = SensitiveDataRedactor.Redact(storage.LogsDirectory),
			["database_path"] = SensitiveDataRedactor.Redact(storage.DatabasePath),
			["database_size"] = dbBytes < 0 ? "不存在" : FormatSize(dbBytes),
			["live2d_effective_quality"] = render?.EffectiveQuality ?? "unknown",
			["live2d_effective_fps"] = render?.EffectiveFps.ToString() ?? "0",
			["live2d_render_scale"] = render?.EffectiveRenderScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "0",
			["live2d_frame_time_p95_ms"] = render?.FrameTimeP95Ms.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "0",
			["live2d_mask_cost_p95_ms"] = render?.MaskCostP95Ms.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "0",
			["live2d_dropped_frames"] = render?.DroppedFrames.ToString() ?? "0",
			["live2d_shadow_applied"] = render?.ShadowApplied.ToString() ?? "false",
		};
	}

	/// <summary>
	/// 把字典拼成可复制的多行文本 (键: 值)
	/// </summary>
	public static string ToText(Dictionary<string, string> info) =>
		string.Join(Environment.NewLine, info.Select(pair => $"{pair.Key}: {pair.Value}"));

	/// <summary>
	/// 取文件大小; 文件不存在返回 -1, 读取失败按 0 处理 (诊断信息不能反过来抛异常)
	/// </summary>
	private static long FileSizeOrDefault(string path)
	{
		try
		{
			FileInfo file = new(path);
			return file.Exists ? file.Length : -1;
		}
		catch (IOException)
		{
			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			return 0;
		}
	}

	private static string FormatDuration(long milliseconds)
	{
		TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);
		return span.Days > 0 ? $"{span.Days}天{span.Hours}小时{span.Minutes}分" : $"{span.Hours}小时{span.Minutes}分{span.Seconds}秒";
	}

	private static string FormatSize(long bytes) => bytes switch
	{
		>= 1L << 30 => $"{bytes >> 30} GB",
		>= 1L << 20 => $"{bytes >> 20} MB",
		>= 1L << 10 => $"{bytes >> 10} KB",
		_ => $"{bytes} B",
	};
}
