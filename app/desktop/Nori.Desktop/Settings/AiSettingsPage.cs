using System.Text.Json;
using Nori.Core.Chat;
using System.Globalization;
using Avalonia.Threading;

namespace Nori.Desktop.Settings;

/// <summary>AI 对话与 Embedding 设置页。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "设置命令回调按 UI 约定显式丢弃受控后台 Task。")]
public sealed class AiSettingsPage : SettingsPageBase
{
	private static readonly IReadOnlyList<SettingsOption> ProviderOptions =
	[
		new("openai", new("OpenAI Chat Completions", "OpenAI Chat Completions")),
		new("openai_responses", new("OpenAI Responses", "OpenAI Responses")),
		new("anthropic", new("Anthropic", "Anthropic")),
		new("google", new("Google Gemini", "Google Gemini")),
	];

	private static readonly IReadOnlyList<SettingsOption> ReasoningOptions =
	[
		new("auto", new("自动 (模型默认)", "Auto (model default)")),
		new("off", new("关闭", "Off")),
		new("minimal", new("最低", "Minimal")),
		new("low", new("低", "Low")),
		new("medium", new("中", "Medium")),
		new("high", new("高", "High")),
		new("xhigh", new("超高", "Extra high")),
		new("max", new("最大", "Max")),
	];

	private static readonly IReadOnlyList<SettingsOption> ContextLimitOptions =
	[
		new("auto", new("自动 (按模型窗口，最多 128K)", "Auto (model window, up to 128K)")),
		new("full", new("模型完整窗口", "Full model window")),
		new("16000", new("16K", "16K")),
		new("32000", new("32K", "32K")),
		new("64000", new("64K", "64K")),
		new("128000", new("128K", "128K")),
		new("200000", new("200K", "200K")),
		new("1000000", new("1M", "1M")),
	];

	private static readonly IReadOnlyList<SettingsOption> ImageProviderOptions =
	[
		new("openai", new("OpenAI 兼容 (Images API)", "OpenAI compatible (Images API)")),
		new("google", new("Google Gemini / Imagen", "Google Gemini / Imagen")),
	];

	private static readonly IReadOnlyList<SettingsOption> ImageSizeOptions =
	[
		new("auto", new("自动", "Auto")),
		new("1:1", new("1:1 方形", "1:1 Square")),
		new("16:9", new("16:9 横向", "16:9 Landscape")),
		new("9:16", new("9:16 纵向", "9:16 Portrait")),
		new("4:3", new("4:3", "4:3")),
		new("3:4", new("3:4", "3:4")),
		new("1024x1024", new("1024×1024", "1024×1024")),
		new("1536x1024", new("1536×1024", "1536×1024")),
		new("1024x1536", new("1024×1536", "1024×1536")),
	];

	private readonly SettingsFieldViewModel _provider;
	private IReadOnlyList<CustomChatProviderDescriptor> _pluginProviders = [];
	private readonly SettingsFieldViewModel _reasoning;
	private readonly SettingsFieldViewModel _modelInfo;
	private readonly SettingsFieldViewModel _contextLimit;
	private string _modelInfoKey = "";
	private int _modelInfoRequest;
	private readonly SettingsFieldViewModel _baseUrl;
	private readonly SettingsFieldViewModel _model;
	private readonly SettingsFieldViewModel _apiKey;
	private readonly SettingsFieldViewModel _persona;
	private readonly SettingsFieldViewModel _embeddingModel;
	private readonly SettingsFieldViewModel _embeddingBaseUrl;
	private readonly SettingsFieldViewModel _embeddingApiKey;
	private readonly SettingsFieldViewModel _embeddingDimensions;
	private readonly List<string> _models = [];
	private readonly SettingsFieldViewModel _modelPicker;
	private readonly SettingsFieldViewModel _chatResult;
	private readonly SettingsFieldViewModel _embeddingResult;
	private bool _chatBusy;
	private bool _embeddingBusy;

	/// <summary>创建 AI 设置页。</summary>
	public AiSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "ai", "self", new("模型服务", "Model providers"), new("配置对话、模型和知识库向量服务。", "Configure chat, model and knowledge embedding providers."), lifetimeToken)
	{
		SettingsSectionViewModel chat = AddSection(new("对话服务", "Chat provider"));
		_provider = AddField(chat, "provider", new("服务商", "Provider"), new("选择兼容的对话 API。", "Choose a compatible chat API."), SettingsEditorKind.Choice,
			snapshot => FirstString(snapshot, "openai", ["ai", "chat", "provider"], ["ai", "provider"]), "openai",
			(value, token) => SaveProviderAsync(Convert.ToString(value) ?? "openai", token),
			options: ProviderOptions);
		_baseUrl = AddField(chat, "baseUrl", new("接口地址", "Base URL"), new("自定义代理或兼容服务时填写。", "Use a custom proxy or compatible endpoint when needed."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, "https://api.openai.com/v1", ["ai", "chat", "baseUrl"], ["ai", "baseUrl"]), "https://api.openai.com/v1",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { chat = new { baseUrl = Convert.ToString(value)?.Trim() ?? string.Empty } }, token));
		_apiKey = AddField(chat, "apiKey", new("API 密钥", "API key"), new("保存后输入框会自动清空。", "The input is cleared after a successful save."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "ai", "chat", "hasApiKey") || SettingsSnapshotReader.SecretConfigured(snapshot, "ai", "hasApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { chat = new { apiKey = Convert.ToString(value)?.Trim() ?? string.Empty } }, token), secret: true);
		_model = AddField(chat, "model", new("对话模型", "Chat model"), new("填写模型 ID，或使用获取模型列表。", "Enter a model ID or fetch the provider model list."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "chat", "model"], ["ai", "model"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { chat = new { model = Convert.ToString(value)?.Trim() ?? string.Empty } }, token));
		_modelPicker = AddField(chat, "modelPicker", new("可用模型", "Available models"), new("获取列表后选择模型，也可在上方填写自定义 ID。", "Select a fetched model or enter a custom ID above."), SettingsEditorKind.Choice,
			_ => _model.Text, string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {chat = new {model = Convert.ToString(value) ?? string.Empty}}, token));
		_modelPicker.IsVisible = false;

		_persona = AddField(chat, "persona", new("个性设定", "Persona"), new("可选的角色和语气说明。", "Optional role and tone instructions."), SettingsEditorKind.Multiline,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "chat", "persona"], ["ai", "persona"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { persona = Convert.ToString(value) ?? string.Empty }, token));

		_reasoning = AddField(chat, "reasoning", new("思考强度", "Reasoning effort"),
			new("思考模型的推理深度。「自动」不发送参数；所选档位不受支持时会就近选择模型支持的档位。", "Reasoning depth for thinking models. Auto sends nothing; unsupported levels snap to the nearest supported one."),
			SettingsEditorKind.Choice,
			snapshot => FirstString(snapshot, "auto", ["ai", "chat", "reasoning"]), "auto",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {chat = new {reasoning = Convert.ToString(value) ?? "auto"}}, token),
			options: ReasoningOptions);
		_modelInfo = AddField(chat, "modelInfo", new("模型信息", "Model info"), new("来自 models.dev 的上下文窗口与能力信息。", "Context window and capabilities from models.dev."), SettingsEditorKind.Multiline,
			_ => _modelInfo?.Text ?? string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		_modelInfo.IsVisible = false;

		AddAction(chat, "fetchModels", new("获取模型", "Fetch models"), new("使用当前服务商列出可用模型。", "List models from the selected provider."), new SettingsCommand(command => _ = FetchModelsAsync()));
		AddAction(chat, "testChat", new("测试对话连接", "Test chat connection"), new("发送一次最小连接测试。", "Run a minimal connection test."), new SettingsCommand(command => _ = TestChatAsync()));

		_chatResult = AddField(chat, "chatResult", new("对话连接结果", "Chat connection result"), new("", ""), SettingsEditorKind.Multiline,
			_ => _chatResult?.Text ?? string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		_chatResult.IsVisible = false;

		SettingsSectionViewModel context = AddSection(new("上下文", "Context"));
		_contextLimit = AddField(context, "contextLimit", new("上下文上限", "Context limit"),
			new("每轮发送给模型的最大上下文。「自动」按模型窗口 (来自 models.dev) 计算，最多 128K，避免长对话产生高额费用。", "Maximum context sent per turn. Auto follows the model window from models.dev, capped at 128K to keep long chats affordable."),
			SettingsEditorKind.Choice,
			snapshot => FirstString(snapshot, "auto", ["ai", "context", "limit"]), "auto",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {context = new {limit = Convert.ToString(value) ?? "auto"}}, token),
			options: ContextLimitOptions);
		AddField(context, "contextCompaction", new("自动压缩上下文", "Auto-compact context"),
			new("对话接近上限时，用当前模型把较早的内容整理成摘要，保留约定与事实；清空聊天记录会一并清除摘要。", "When the conversation nears the limit, older turns are summarized by the current model. Clearing chat history also clears the summary."),
			SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "ai", "context", "compaction"), true,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {context = new {compaction = Convert.ToBoolean(value)}}, token));

		SettingsSectionViewModel image = AddSection(new("图像生成", "Image generation"));
		AddField(image, "imageStatus", new("状态", "Status"), new("配置完成后，可以在对话中让 Nori 画图；每次生成前都会请求确认。", "Once configured, ask Nori to draw in chat; each generation asks for confirmation first."), SettingsEditorKind.Multiline,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "ai", "image", "configured")
				? Text("已启用：对话中可以让 Nori 生成图片。", "Enabled: Nori can generate images in chat.")
				: Text("未启用：请填写图像模型；地址和密钥留空时沿用同协议的对话服务。", "Disabled: enter an image model. Empty URL/key reuse the chat provider when the protocol matches."),
			string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(image, "imageProvider", new("协议", "Protocol"), new("OpenAI 兼容服务使用 /images/generations；Google 支持 Gemini 图像模型与 Imagen。", "OpenAI-compatible services use /images/generations; Google supports Gemini image models and Imagen."), SettingsEditorKind.Choice,
			snapshot => FirstString(snapshot, "openai", ["ai", "image", "provider"]), "openai",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {image = new {provider = Convert.ToString(value) ?? "openai"}}, token),
			options: ImageProviderOptions);
		AddField(image, "imageBaseUrl", new("接口地址", "Base URL"), new("留空时沿用对话服务地址 (仅同一协议)。", "Leave empty to reuse the chat provider URL (same protocol only)."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "image", "baseUrl"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {image = new {baseUrl = Convert.ToString(value)?.Trim() ?? string.Empty}}, token));
		AddField(image, "imageApiKey", new("API 密钥", "API key"), new("留空时沿用对话服务密钥 (仅同一协议)；保存后输入框自动清空。", "Leave empty to reuse the chat key (same protocol only). The input clears after saving."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "ai", "image", "hasApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {image = new {apiKey = Convert.ToString(value)?.Trim() ?? string.Empty}}, token), secret: true);
		AddField(image, "imageModel", new("图像模型", "Image model"), new("例如 gpt-image-1、dall-e-3、gemini-2.5-flash-image、imagen-4.0-generate-001。", "For example gpt-image-1, dall-e-3, gemini-2.5-flash-image or imagen-4.0-generate-001."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "image", "model"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {image = new {model = Convert.ToString(value)?.Trim() ?? string.Empty}}, token));
		AddField(image, "imageSize", new("默认尺寸", "Default size"), new("请求未指定比例时使用。", "Used when the request does not specify an aspect ratio."), SettingsEditorKind.Choice,
			snapshot => FirstString(snapshot, "auto", ["ai", "image", "size"]), "auto",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {image = new {size = Convert.ToString(value) ?? "auto"}}, token),
			options: ImageSizeOptions);

		SettingsSectionViewModel embedding = AddSection(new("向量服务", "Embedding provider"));
		_embeddingModel = AddField(embedding, "embeddingModel", new("Embedding 模型", "Embedding model"), new("用于记忆和知识库检索。", "Used for memory and knowledge retrieval."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, "BAAI/bge-m3", ["ai", "embedding", "model"], ["embedding", "model"]), "BAAI/bge-m3",
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { embedding = new { model = Convert.ToString(value)?.Trim() ?? string.Empty } }, token));
		_embeddingBaseUrl = AddField(embedding, "embeddingBaseUrl", new("Embedding 地址", "Embedding base URL"), new("留空时使用服务默认地址。", "Leave empty to use the provider default."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "embedding", "baseUrl"], ["embedding", "baseUrl"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { embedding = new { baseUrl = Convert.ToString(value)?.Trim() ?? string.Empty } }, token));
		_embeddingApiKey = AddField(embedding, "embeddingApiKey", new("Embedding 密钥", "Embedding API key"), new("保存成功后输入框自动清空。", "The input is cleared after a successful save."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "ai", "embedding", "hasApiKey") || SettingsSnapshotReader.SecretConfigured(snapshot, "embedding", "hasApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new { embedding = new { apiKey = Convert.ToString(value)?.Trim() ?? string.Empty } }, token), secret: true);
		_embeddingDimensions = AddField(embedding, "embeddingDimensions", new("向量维度", "Dimensions"), new("留空使用模型默认维度；自定义维度必须为正整数。", "Leave empty for model defaults, or enter a positive integer."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, string.Empty, ["ai", "embedding", "dimensions"], ["embedding", "dimensions"]), string.Empty,
			(value, token) => ExecuteAsync("settings_update_ai_providers", new {embedding = new {dimensions = NormalizeDimensions(Convert.ToString(value))}}, token));
		AddAction(embedding, "testEmbedding", new("测试向量连接", "Test embedding connection"), new("检查 Embedding 地址、密钥和模型。", "Check the embedding endpoint, key and model."), new SettingsCommand(command => _ = TestEmbeddingAsync()));
		_embeddingResult = AddField(embedding, "embeddingResult", new("向量连接结果", "Embedding connection result"), new("", ""), SettingsEditorKind.Multiline,
			_ => _embeddingResult?.Text ?? string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		_embeddingResult.IsVisible = false;
	}

	internal override void ApplySnapshot(JsonElement snapshot)
	{
		_pluginProviders = ReadCustomProviders(snapshot);
		base.ApplySnapshot(snapshot);
		UpdateProviderOptions();
		UpdateModelOptions();
		EnsureCurrentOption(_contextLimit, ContextLimitOptions);
		_ = RefreshModelInfoAsync(snapshot);
	}

	/// <summary>内置协议 + 当前活跃插件提供商；撤销后选中项仍保留，避免静默改成 OpenAI。</summary>
	private void UpdateProviderOptions()
	{
		List<SettingsOption> options = [.. ProviderOptions];
		options.AddRange(_pluginProviders.Select(provider => new SettingsOption(provider.Id, new(provider.Name, provider.Name))));
		string selected = _provider.Selected;
		if (selected.Length > 0 && selected.StartsWith("plugin:", StringComparison.Ordinal) && !options.Any(option => option.Value == selected))
			options.Add(new SettingsOption(selected, new(Text("插件提供商 (当前不可用)", "Plugin provider (unavailable)"), Text("插件提供商 (当前不可用)", "Plugin provider (unavailable)"))));
		_provider.SetOptions(options);
	}

	/// <summary>快照只带插件提供商的公开元数据。</summary>
	private static IReadOnlyList<CustomChatProviderDescriptor> ReadCustomProviders(JsonElement snapshot)
	{
		if (!snapshot.TryGetProperty("ai", out JsonElement ai) || !ai.TryGetProperty("providers", out JsonElement providers) || providers.ValueKind != JsonValueKind.Array)
			return [];
		List<CustomChatProviderDescriptor> result = [];
		foreach (JsonElement item in providers.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object) continue;
			string id = SettingsSnapshotReader.String(item, string.Empty, "id");
			string name = SettingsSnapshotReader.String(item, string.Empty, "name");
			if (!id.StartsWith("plugin:", StringComparison.Ordinal) || name.Length == 0) continue;
			bool requiresApiKey = !item.TryGetProperty("requiresApiKey", out JsonElement required) || required.ValueKind != JsonValueKind.False;
			result.Add(new CustomChatProviderDescriptor(id, name, SettingsSnapshotReader.String(item, string.Empty, "defaultBaseUrl"), requiresApiKey));
		}
		return result;
	}

	/// <summary>配置里的自定义值不在预设选项中时补一项，避免选择框显示为空。</summary>
	private static void EnsureCurrentOption(SettingsFieldViewModel field, IReadOnlyList<SettingsOption> presets)
	{
		string current = field.Selected;
		if (current.Length == 0 || presets.Any(option => option.Value == current))
		{
			if (field.Options.Count != presets.Count) field.SetOptions(presets);
			return;
		}
		string label = Nori.Core.Agent.ContextWindowPlanner.TryParseTokens(current, out int tokens)
			? FormatTokens(tokens)
			: current;
		field.SetOptions(presets.Append(new SettingsOption(current, new(label, label))));
	}

	/// <summary>模型、协议、地址或上下文设置变化后重新查询 models.dev 能力信息。</summary>
	private async Task RefreshModelInfoAsync(JsonElement snapshot)
	{
		string provider = FirstString(snapshot, "openai", ["ai", "chat", "provider"], ["ai", "provider"]);
		string baseUrl = FirstString(snapshot, string.Empty, ["ai", "chat", "baseUrl"], ["ai", "baseUrl"]);
		string model = FirstString(snapshot, string.Empty, ["ai", "chat", "model"], ["ai", "model"]);
		string limit = FirstString(snapshot, "auto", ["ai", "context", "limit"]);
		string key = string.Join("|", provider, baseUrl, model, limit, SettingsLocalization.IsEnglish ? "en" : "zh");
		if (key == _modelInfoKey) return;
		_modelInfoKey = key;
		int request = ++_modelInfoRequest;
		if (model.Length == 0)
		{
			_modelInfo.Text = string.Empty;
			_modelInfo.IsVisible = false;
			return;
		}
		try
		{
			JsonElement info = await ExecuteAsync("llm_model_info", new {provider, baseUrl, model}, LifetimeToken).ConfigureAwait(true);
			if (request != _modelInfoRequest) return;
			_modelInfo.Text = DescribeModel(info);
			_modelInfo.IsVisible = _modelInfo.Text.Length > 0;
		}
		catch (OperationCanceledException) { }
		catch (Exception)
		{
			// 模型信息只是提示，查询失败不打扰用户；下次快照变化时重试。
			if (request == _modelInfoRequest) _modelInfoKey = "";
		}
	}

	/// <summary>把 llm_model_info 结果整理成一段可读说明。</summary>
	internal static string DescribeModel(JsonElement info)
	{
		if (info.ValueKind != JsonValueKind.Object) return string.Empty;
		List<string> lines = [];
		bool found = info.TryGetProperty("found", out JsonElement foundElement) && foundElement.ValueKind == JsonValueKind.True;
		if (found)
		{
			int window = Int(info, "inputLimit") is > 0 and var input ? input : Int(info, "contextWindow");
			List<string> facts = [];
			if (window > 0) facts.Add(Text($"上下文窗口 {FormatTokens(window)}", $"Context {FormatTokens(window)}"));
			if (Int(info, "outputLimit") is > 0 and var output) facts.Add(Text($"最大输出 {FormatTokens(output)}", $"Max output {FormatTokens(output)}"));
			if (Bool(info, "imageInput")) facts.Add(Text("支持识图", "Vision"));
			if (Bool(info, "toolCall")) facts.Add(Text("支持工具", "Tools"));
			lines.Add(string.Join(" · ", facts));
			if (Bool(info, "reasoning"))
			{
				List<string> efforts = info.TryGetProperty("reasoningEfforts", out JsonElement values) && values.ValueKind == JsonValueKind.Array
					? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToList()
					: [];
				string detail = efforts.Count > 0
					? string.Join(" / ", efforts)
					: Bool(info, "reasoningBudget") ? Text("按预算", "token budget") : Text("仅开关", "on/off only");
				lines.Add(Text($"思考模型：{detail}", $"Reasoning model: {detail}"));
			}
			else
			{
				lines.Add(Text("非思考模型：思考强度设置不会生效。", "Not a reasoning model: the reasoning setting has no effect."));
			}
		}
		else
		{
			lines.Add(Text("models.dev 中没有找到该模型，按保守默认值 (32K) 处理上下文。", "Model not found on models.dev; a conservative 32K context is assumed."));
		}
		if (info.TryGetProperty("context", out JsonElement context) && context.ValueKind == JsonValueKind.Object && Int(context, "limit") is > 0 and var limit)
			lines.Add(Text($"本机每轮上下文上限：{FormatTokens(limit)}", $"Per-turn context limit: {FormatTokens(limit)}"));
		return string.Join("\n", lines.Where(line => line.Length > 0));
	}

	private static int Int(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;

	private static bool Bool(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

	/// <summary>token 数的简写 (128000 → 128K，1048576 → 1M)。</summary>
	internal static string FormatTokens(int tokens) => tokens switch
	{
		>= 1_000_000 => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
		>= 1_000 => Math.Round(tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "K",
		_ => tokens.ToString(CultureInfo.InvariantCulture),
	};

	private void UpdateModelOptions()
	{
		IEnumerable<string> values = new[] {_model.Text}.Concat(_models).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal);
		_modelPicker.SetOptions(values.Select(value => new SettingsOption(value, new(value, value))));
		_modelPicker.IsVisible = _models.Count > 0;
	}

	private async Task<JsonElement> SaveProviderAsync(string provider, CancellationToken cancellationToken)
	{
		string address = await Dispatcher.UIThread.InvokeAsync(() => DefaultBaseUrl(provider, _baseUrl.Text, _pluginProviders));
		JsonElement result = await ExecuteAsync("settings_update_ai_providers", new {chat = new {provider, baseUrl = address}}, cancellationToken).ConfigureAwait(false);
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_models.Clear();
			UpdateModelOptions();
		});
		return result;
	}

	/// <summary>更换协议时只替换已知默认地址，保留自定义代理。</summary>
	internal static string DefaultBaseUrl(string provider, string current) => DefaultBaseUrl(provider, current, []);

	/// <summary>插件提供商的地址来自其元数据；空默认地址表示不需要地址。</summary>
	internal static string DefaultBaseUrl(string provider, string current, IReadOnlyList<CustomChatProviderDescriptor> customProviders)
	{
		Dictionary<string, string> defaults = new(StringComparer.Ordinal)
		{
			["openai"] = "https://api.openai.com/v1",
			["openai_responses"] = "https://api.openai.com/v1",
			["anthropic"] = "https://api.anthropic.com/v1",
			["google"] = "https://generativelanguage.googleapis.com/v1beta",
		};
		string address = current.Trim();
		// 只替换空值或已知默认地址（内置协议与插件的默认值），保留自定义代理。
		bool replace = address.Length == 0
			|| defaults.Values.Any(value => string.Equals(value, address, StringComparison.OrdinalIgnoreCase))
			|| customProviders.Any(item => item.DefaultBaseUrl.Length > 0 && string.Equals(item.DefaultBaseUrl, address, StringComparison.OrdinalIgnoreCase));
		if (!provider.StartsWith("plugin:", StringComparison.Ordinal))
			return replace && defaults.TryGetValue(provider, out string? next) ? next : address;
		string? pluginDefault = customProviders.FirstOrDefault(item => item.Id == provider)?.DefaultBaseUrl;
		return replace ? pluginDefault ?? "" : address;
	}

	/// <summary>校验可选向量维度，空值表示恢复模型默认。</summary>
	internal static string NormalizeDimensions(string? value)
	{
		string text = value?.Trim() ?? string.Empty;
		if (text.Length == 0) return string.Empty;
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int dimensions) || dimensions <= 0)
			throw new ArgumentException("向量维度必须为正整数，或留空使用默认值。");
		return dimensions.ToString(CultureInfo.InvariantCulture);
	}

	private async Task FetchModelsAsync()
	{
		if (_chatBusy) return;
		SetActionsBusy(false, true);
		try
		{
			if (!await FlushConnectionSettingsAsync(false, LifetimeToken).ConfigureAwait(true)) return;
			ApplySnapshot(await Service.GetSnapshotAsync(LifetimeToken).ConfigureAwait(true));
			string provider = _provider.Selected;
			string baseUrl = _baseUrl.Text.Trim();
			if (baseUrl.Length == 0) throw new InvalidOperationException("请先填写接口地址。");
			JsonElement result = await ExecuteAsync("llm_fetch_models", new {provider, baseUrl}, LifetimeToken).ConfigureAwait(true);
			_models.Clear();
			if (result.ValueKind == JsonValueKind.Array)
				_models.AddRange(result.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
					.Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
			UpdateModelOptions();
			if (_models.Count == 0) SetConnectionResult(false, Text("没有找到可用模型。", "No models were returned."));
			else SetConnectionResult(false, Text($"已找到 {_models.Count} 个模型，可在列表中选择。", $"{_models.Count} models available to select."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetConnectionResult(false, SettingsErrorText.Resolve(exception)); }
		finally { SetActionsBusy(false, false); }
	}

	private void SetActionsBusy(bool embedding, bool busy)
	{
		if (embedding) _embeddingBusy = busy;
		else _chatBusy = busy;
		foreach (SettingsFieldViewModel field in Sections.SelectMany(section => section.Fields))
		{
			if (embedding && field.Key == "testEmbedding") field.IsReadOnly = busy;
			if (!embedding && field.Key is "fetchModels" or "testChat") field.IsReadOnly = busy;
		}
		if (busy) SetConnectionResult(embedding, Text("正在连接服务…", "Connecting to the provider…"));
	}

	/// <summary>连接探测只保存本组字段，另一组无效草稿不会阻断当前操作。</summary>
	internal async Task<bool> FlushConnectionSettingsAsync(bool embedding, CancellationToken cancellationToken = default)
	{
		SettingsFieldViewModel[] fields = embedding
			? [_embeddingModel, _embeddingBaseUrl, _embeddingApiKey, _embeddingDimensions]
			: [_baseUrl, _apiKey, _model, _modelPicker, _provider];
		foreach (SettingsFieldViewModel field in fields)
		{
			if (await field.FlushPendingSavesAsync(cancellationToken).ConfigureAwait(true)) continue;
			SetConnectionResult(embedding, field.Label + ": " + field.ErrorText);
			return false;
		}
		return true;
	}

	/// <summary>将探测结果保留在对应卡片中，不覆盖另一组结果。</summary>
	internal void SetConnectionResult(bool embedding, string message)
	{
		SettingsFieldViewModel field = embedding ? _embeddingResult : _chatResult;
		string localized = SettingsErrorText.Resolve(message);
		field.Text = localized;
		field.IsVisible = localized.Length > 0;
	}

	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;

	private async Task TestChatAsync()
	{
		if (_chatBusy) return;
		SetActionsBusy(false, true);
		try
		{
			if (!await FlushConnectionSettingsAsync(false, LifetimeToken).ConfigureAwait(true)) return;
			ApplySnapshot(await Service.GetSnapshotAsync(LifetimeToken).ConfigureAwait(true));
			JsonElement result = await ExecuteAsync("ai_test_connection", new
			{
				target = "chat",
				provider = _provider.Selected,
				baseUrl = _baseUrl.Text.Trim(),
				model = _model.Text.Trim(),
			}, LifetimeToken).ConfigureAwait(true);
			SetConnectionResult(false, ConnectionStatus(result));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetConnectionResult(false, SettingsErrorText.Resolve(exception)); }
		finally { SetActionsBusy(false, false); }
	}

	private async Task TestEmbeddingAsync()
	{
		if (_embeddingBusy) return;
		SetActionsBusy(true, true);
		try
		{
			if (!await FlushConnectionSettingsAsync(true, LifetimeToken).ConfigureAwait(true)) return;
			ApplySnapshot(await Service.GetSnapshotAsync(LifetimeToken).ConfigureAwait(true));
			JsonElement result = await ExecuteAsync("ai_test_connection", new
			{
				target = "embedding",
				baseUrl = _embeddingBaseUrl.Text.Trim(),
				model = _embeddingModel.Text.Trim(),
				dimensions = NormalizeDimensions(_embeddingDimensions.Text),
			}, LifetimeToken).ConfigureAwait(true);
			SetConnectionResult(true, ConnectionStatus(result));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetConnectionResult(true, SettingsErrorText.Resolve(exception)); }
		finally { SetActionsBusy(true, false); }
	}

	private static string ConnectionStatus(JsonElement result)
	{
		if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True)
			return Text("连接成功。", "Connection succeeded.");
		if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message", out JsonElement message))
			return message.GetString() ?? Text("连接失败。", "Connection failed.");
		return Text("连接失败。", "Connection failed.");
	}

	private static string FirstString(JsonElement root, string fallback, params string[][] paths)
	{
		foreach (string[] path in paths)
		{
			string value = SettingsSnapshotReader.String(root, string.Empty, path);
			if (value.Length > 0) return value;
		}
		return fallback;
	}

}
