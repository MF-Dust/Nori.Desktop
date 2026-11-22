using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Nori.Core.Logging;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Runtime;
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
			() => DiagnosticExporter.Export(targetPath, _services.Logger, _services.PetRuntime, _services.Paths, _services.SafeMode, cancellationToken, _services.AgentTrace,
				_services.AgentExecution, _services.VoiceMetrics.CaptureSnapshot()),
			cancellationToken);
		return new {fileName = result.FileName, bytes = result.Bytes, skipped = result.Skipped};
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
