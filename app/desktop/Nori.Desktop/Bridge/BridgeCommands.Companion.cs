using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Mcp;
using Nori.Core.Resources;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private async Task<object?> TtsTestAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string text = OptionalStr(args, "text") is {Length: > 0} custom ? custom : "你好呀！我是 Nori，这是一条声音播放测试~";
		await Runtime.Voice.SpeakAsync(text);
		return null;
	}

	private async Task<object?> SttStartAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		await Runtime.Voice.StartListeningAsync(cancellationToken);
		return null;
	}

	private async Task<object?> SttStopAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		string text = await Runtime.Voice.StopListeningAndTranscribeAsync(cancellationToken);
		return new {text};
	}

	private async Task<object?> ModelImportLocalAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () => (object?)true);
		if (source is INativeModelSource) await RequireVisibleMainVoidAsync(source);
		return await ImportLocalResourceAsync(source, args, cancellationToken);
	}

	/// <summary>读取已保存的模型表情选择，优先模型键并兼容旧全局键。</summary>
	private IReadOnlyList<string> ReadSelectedExpressions(string modelId) =>
		ReadExpressionConfig($"l2d_expression_{modelId}") ?? ReadExpressionConfig("l2d_expression") ?? [];

	private string[]? ReadExpressionConfig(string key)
	{
		ConfigValue? value = _services.Config.Get(key);
		if (value is null) return null;
		try
		{
			string[]? expressions = value switch
			{
				ConfigValue.Json json => json.Value.Deserialize<string[]>(BridgeJson.Options),
				ConfigValue.Text text => JsonSerializer.Deserialize<string[]>(text.Value, BridgeJson.Options),
				_ => null,
			};
			return expressions?.Where(expression => !string.IsNullOrWhiteSpace(expression)).ToArray();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>读取指定模型的互动配置; 损坏配置按空配置处理并记录日志。</summary>
	private PetInteractionConfig ReadInteractionConfig(string modelId)
	{
		if (_services.Config.Get(PetInteractionConfig.StorageKey(modelId)) is not ConfigValue.Json {Value: JsonNode node})
		{
			return PetInteractionConfig.Empty;
		}

		try
		{
			return PetInteractionConfig.Parse(node.ToJsonString(PetInteractionJson.Options));
		}
		catch (Exception exception)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"读取模型互动配置失败 [{modelId}]: {exception.GetType().Name}");
			return PetInteractionConfig.Empty;
		}
	}

	/// <summary>
	/// 写入指定模型的互动配置。
	/// 前端调用: invoke("model_set_interactions", {modelId, interactions})
	/// </summary>
	private async Task<object?> ModelSetInteractionsAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
		if (!args.TryGetProperty("interactions", out JsonElement interactionsElement)
			|| interactionsElement.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidOperationException("互动配置不能为空");
		}

		PetInteractionConfig config;
		try
		{
			config = PetInteractionConfig.Parse(interactionsElement.GetRawText());
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			throw new InvalidOperationException($"互动配置无效: {exception.Message}", exception);
		}

		string dir = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
		Model3MetaInfo meta = Model3Meta.Read(dir);
		config.ValidateBindings(meta.Motions, meta.Expressions);
		_services.Config.Set(PetInteractionConfig.StorageKey(modelId), new ConfigValue.Json(config.ToJsonNode()));
		_services.PetRuntime?.SetInteractionConfig(modelId, config);
		Runtime.InvalidateSnapshot();
		await Task.CompletedTask;
		return null;
	}

	/// <summary>
	/// 模型显示参数写入 (缩放按模型存储, 表情列表同)
	/// </summary>
	private async Task<object?> ModelSetDisplayAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
		if (args.TryGetProperty("scale", out JsonElement scaleElem))
		{
			ApplyDisplayKey($"l2d_scale_{modelId}", ReadFiniteNumber(scaleElem, "模型缩放", 0.1f, 4.0f));
		}
		if (args.TryGetProperty("opacity", out JsonElement opacityElem))
		{
			ApplyDisplayKey($"l2d_opacity_{modelId}", ReadFiniteNumber(opacityElem, "模型透明度", 0.0f, 1.0f));
		}
		if (args.TryGetProperty("renderScale", out JsonElement renderScaleElem))
		{
			ApplyDisplayKey($"l2d_render_scale_{modelId}", ReadFiniteNumber(renderScaleElem, "渲染倍率",
				Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale));
		}
		if (args.TryGetProperty("qualityMode", out JsonElement qualityElem))
		{
			string qualityMode = qualityElem.ValueKind == JsonValueKind.String ? qualityElem.GetString() ?? "" : "";
			Live2DQualityMode mode = Live2DRenderSettings.ParseQualityMode(qualityMode)
				?? throw new InvalidOperationException("质量模式只能是 adaptive、quality 或 eco");
			ApplyDisplayKey($"l2d_quality_mode_{modelId}", Live2DRenderSettings.QualityModeToStorage(mode));
		}
		if (args.TryGetProperty("maxFps", out JsonElement maxFpsElem))
		{
			ApplyDisplayKey($"l2d_max_fps_{modelId}", ReadInteger(maxFpsElem, "最大帧率", 0, Live2DRenderSettings.MaxExplicitFps));
		}
		if (args.TryGetProperty("shadow", out JsonElement shadowElem))
		{
			if (shadowElem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
				throw new InvalidOperationException("阴影开关必须是布尔值");
			ApplyDisplayKey($"l2d_shadow_{modelId}", shadowElem.GetBoolean() ? "1" : "0");
		}
		if (args.TryGetProperty("expressions", out JsonElement expElem))
		{
			if (expElem.ValueKind != JsonValueKind.Array || expElem.GetArrayLength() > 64)
				throw new InvalidOperationException("表情列表格式无效或数量超过上限");
			HashSet<string> available = Model3Meta.Read(_services.Resources.ResourceDir(ResourceType.Live2D, modelId))
				.Expressions.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (JsonElement expression in expElem.EnumerateArray())
			{
				string name = expression.ValueKind == JsonValueKind.String ? expression.GetString() ?? "" : "";
				if (name.Length == 0 || name.Length > 128 || !available.Contains(name))
					throw new InvalidOperationException($"模型不包含表情: {name}");
			}
			ApplyDisplayKey($"l2d_expression_{modelId}", expElem.GetRawText());
		}
		await Task.CompletedTask;
		Runtime.InvalidateSnapshot();
		return null;
	}

	private void ApplyDisplayKey(string key, string storage)
	{
		UpdateConfigDirect(key, storage);
		ApplyPetConfig(key, storage);
	}

	/// <summary>行为开关批量写入并热应用</summary>
	private async Task<object?> ModelSetBehaviorAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		SetBehaviorKey(args, "clickInteraction", "l2d_click_interaction");
		SetBehaviorKey(args, "clickThrough", "l2d_click_through");
		SetBehaviorKey(args, "autoBlink", "l2d_auto_blink");
		SetBehaviorKey(args, "eyeTracking", "l2d_eye_tracking");
		SetBehaviorKey(args, "idleEyeAnimation", "l2d_idle_eye_animation");
		SetBehaviorKey(args, "idleAnimation", "l2d_idle_animation");
		SetBehaviorKey(args, "expressionEnabled", "l2d_expression_enabled");
		SetBehaviorKey(args, "lipSync", "l2d_lip_sync");
		SetBehaviorKey(args, "beatSync", "l2d_beat_sync");
		SetBehaviorKey(args, "aiInteraction", PetInteractionConfig.AiEnabledKey);
		await Task.CompletedTask;
		Runtime.InvalidateSnapshot();
		return null;
	}

	// ===================================================================
	// 伴侣状态
	// ===================================================================

	/// <summary>
	/// 选择 IndexTTS 音频模板文件并写入配置 (仅保存路径, 不克隆)。
	/// </summary>
	private async Task<object?> IndexTtsPickTemplateAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? filePath = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IReadOnlyList<IStorageFile> files = await self.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = "选择 IndexTTS 音频模板 (wav/mp3, 5-30 秒)",
				AllowMultiple = false,
				FileTypeFilter =
				[
					new FilePickerFileType("音频模板 (wav/mp3)") {Patterns = ["*.wav", "*.mp3"]},
				],
			});
			return files.Count > 0 ? files[0].Path.LocalPath : null;
		});
		if (string.IsNullOrWhiteSpace(filePath)) return null;
		_services.Config.Set("indextts_template_audio", new Nori.Core.Configuration.ConfigValue.Text(filePath));
		Runtime.InvalidateSnapshot();
		return filePath!;
	}

	/// <summary>
	/// 上传 IndexTTS 音频模板克隆音色，返回克隆后的 voice_id。
	/// 未传 filePath 时使用配置中的模板路径。
	/// </summary>
	private async Task<object?> IndexTtsCloneVoiceAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		string templatePath = OptionalStr(args, "filePath") ?? "";
		if (string.IsNullOrWhiteSpace(templatePath))
		{
			templatePath = _services.Config.GetStringOr("indextts_template_audio", "");
		}
		if (string.IsNullOrWhiteSpace(templatePath)) throw new InvalidOperationException("请先选择 IndexTTS 音频模板文件");

		Nori.Core.Voice.IndexTtsProvider provider =
			new(_services.Http, _services.Config, _services.Paths);
		string voiceId = await provider.CloneVoiceAsync(templatePath, cancellationToken);
		Runtime.InvalidateSnapshot();
		return new {voiceId};
	}

	/// <summary>
	/// 从本地 ZIP 文件或目录导入资源
	/// </summary>
	private async Task<object?> ImportLocalResourceAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string sourceKind = (OptionalStr(args, "sourceKind") ?? "zip").Trim().ToLowerInvariant();
		if (sourceKind is not ("zip" or "folder")) throw new InvalidOperationException("导入来源只能是 zip 或 folder");

		string? filePath = OptionalStr(args, "filePath");
		if (string.IsNullOrWhiteSpace(filePath))
		{
			Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
			filePath = await _uiDispatcher.InvokeTaskAsync(async () =>
			{
				if (sourceKind == "folder")
				{
					IReadOnlyList<IStorageFolder> folders = await self.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
					{
						Title = "选择 Live2D 模型文件夹",
						AllowMultiple = false,
					});
					return folders.Count > 0 ? folders[0].Path.LocalPath : null;
				}

				IReadOnlyList<IStorageFile> files = await self.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
				{
					Title = "选择 Live2D 资源文件 (.zip)",
					AllowMultiple = false,
					FileTypeFilter =
					[
						new FilePickerFileType("Live2D 压缩包 (*.zip)") {Patterns = ["*.zip"]},
					],
				});
				return files.Count > 0 ? files[0].Path.LocalPath : null;
			});
		}

		if (string.IsNullOrWhiteSpace(filePath)) return null;
		if (sourceKind == "folder" && !Directory.Exists(filePath)) throw new InvalidOperationException("选择的 Live2D 文件夹不存在");
		if (sourceKind == "zip" && (!File.Exists(filePath) || !filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
			throw new InvalidOperationException("请选择有效的 Live2D ZIP 文件");

		const ResourceType type = ResourceType.Live2D;
		IReadOnlyList<string> imported = await Task.Run(
			() => _services.Resources.Import(type, filePath, cancellationToken),
			cancellationToken);
		_services.Logger.Write(LogSource.Backend, "info", $"成功导入本地 Live2D 资源: {string.Join(", ", imported)}");
		Runtime.InvalidateSnapshot();
		return imported;
	}

	private async Task<object?> CallMcpToolCoreAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		string serverId = Str(args, "serverId");
		string toolName = Str(args, "toolName");
		JsonObject? toolArgs = null;
		if (args.TryGetProperty("arguments", out JsonElement argElem) && argElem.ValueKind == JsonValueKind.Object)
		{
			toolArgs = JsonNode.Parse(argElem.GetRawText()) as JsonObject;
		}

		// 带 sessionId 的 MCP 调用可被宿主取消注册表取消
		string? sessionId = OptionalStr(args, "sessionId");
		McpToolResult result;
		if (string.IsNullOrEmpty(sessionId))
		{
			result = await _services.Mcp.CallToolAsync(serverId, toolName, toolArgs, cancellationToken);
		}
		else
		{
			CancellationTokenSource registered = _services.AgentOperations.Register(source.Label, sessionId, cancellationToken);
			try
			{
				result = await _services.Mcp.CallToolAsync(serverId, toolName, toolArgs, registered.Token);
			}
			finally
			{
				_services.AgentOperations.Complete(source.Label, sessionId, registered);
			}
		}

		if (result.IsError) throw new InvalidOperationException(result.AsText());
		return result;
	}
}
