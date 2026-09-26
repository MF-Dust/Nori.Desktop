using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Nori.Core.Logging;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Runtime;
using Nori.Desktop.Telemetry;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	// ===================================================================
	// 调试 / 外壳
	// ===================================================================

	/// <summary>在资源管理器中打开日志目录 (只开放固定目录)</summary>
	private void OpenLogFolder()
	{
		Directory.CreateDirectory(_services.Paths.LogsDirectory);
		ShellOpen.OpenDataDirectory(_services.Paths.LogsDirectory, _services.Paths.DataRoot);
	}

	/// <summary>弹出保存位置并在后台生成脱敏诊断 ZIP。</summary>
	private async Task<object?> ExportDiagnosticsAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		Window self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? targetPath = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IStorageFile? file = await self.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "导出 Nori 诊断信息",
				SuggestedFileName = $"nori-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
				ShowOverwritePrompt = true,
				FileTypeChoices =
				[
					new FilePickerFileType("诊断压缩包 (*.zip)") {Patterns = ["*.zip"]},
				],
			});
			return file?.Path.LocalPath;
		});

		if (string.IsNullOrWhiteSpace(targetPath)) return null;
		DiagnosticExporter.Result result = await Task.Run(
			() => DiagnosticExporter.Export(targetPath, _services.Logger, _services.PetRuntime, _services.Paths, _services.SafeMode, cancellationToken, _services.AgentTrace),
			cancellationToken);
		return new {fileName = result.FileName, bytes = result.Bytes, skipped = result.Skipped};
	}

	private object? RunGcCollect()
	{
		long before = GC.GetTotalMemory(false);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
		long after = GC.GetTotalMemory(true);
		long released = Math.Max(0, before - after);
		_services.Logger.Write(LogSource.Backend, "info", $"调试垃圾回收完成: 释放 {released} 字节");
		return new {released_bytes = released};
	}

	private object? DebugCrashTest(string mode)
	{
		if (SentryTelemetry.IsProductionBuild)
			throw new InvalidOperationException("生产环境不支持调试崩溃测试");

		switch (mode)
		{
			case "ui_thread":
				_uiDispatcher.Post(() => throw new InvalidOperationException("调试崩溃测试: UI 线程未处理异常"));
				break;
			case "background_thread":
				new Thread(() => throw new InvalidOperationException("调试崩溃测试: 后台线程未处理异常")).Start();
				break;
			case "unobserved_task":
				_ = Task.Run(() => throw new InvalidOperationException("调试崩溃测试: 未观察任务异常"));
				break;
			default:
				throw new InvalidOperationException($"未知的崩溃测试模式: {mode}");
		}
		return null;
	}

	/// <summary>读取日志健康状态。前端调用：invoke("get_logging_status")</summary>
	private object GetLoggingStatus() => _services.Logger.GetStatus();

	/// <summary>临时调整日志级别。前端调用：invoke("set_logging_level", {level: "debug"})</summary>
	private object? SetLoggingLevel(JsonElement args)
	{
		string level = Str(args, "level");
		if (!FileLogger.IsLevel(level)) throw new InvalidOperationException("日志级别无效");
		_services.Logger.SetMinimumLevel(level);
		return null;
	}

	/// <summary>记录白名单事件，窗口身份由宿主赋值。前端调用：invoke("write_log", {level: "error", eventId: "audio.error", message: ""})</summary>
	private object? WriteFrontendLog(IBridgeSource source, JsonElement args)
	{
		string level = Str(args, "level").Trim().ToLowerInvariant();
		if (!FileLogger.IsLevel(level))
			throw new InvalidOperationException("日志级别无效");
		string eventId = OptionalStr(args, "eventId") ?? "";
		bool audioHost = source.Label == WindowLabels.AudioHost;
		if (audioHost
			? eventId is not ("audio.error" or "logging.suppressed")
			: eventId is not ("audio.error" or "logging.suppressed" or "diagnostics.test"))
			throw new InvalidOperationException("日志事件无效");
		string message = eventId switch
		{
			"logging.suppressed" => $"重复前端事件已被限流：{Math.Clamp(OptionalInt(args, "suppressedCount") ?? 1, 1, 1_000_000)}",
			"diagnostics.test" => "调试日志链路正常",
			_ => "前端音频操作失败",
		};
		string errorType = OptionalStr(args, "errorType") ?? "";
		if (errorType is "Error" or "TypeError" or "RangeError" or "ReferenceError" or "SyntaxError" or "URIError" or "EvalError" or "AggregateError")
			message += $"：{errorType}";
		bool native = source.Label == "settings";
		_services.Logger.Write(native ? LogSource.Backend : LogSource.Frontend, level, message,
			native ? "NativeSettings" : "Frontend", eventId, windowLabel: source.Label);
		return null;
	}

	private async Task<object?> WriteClipboardAsync(IBridgeSource source, string text)
	{
		await OnUiAsync(async () =>
		{
			Avalonia.Input.Platform.IClipboard clipboard = TopLevel.GetTopLevel(source.Self)?.Clipboard
				?? throw new InvalidOperationException("剪贴板不可用");
			await clipboard.SetTextAsync(text);
		});
		return null;
	}
}
