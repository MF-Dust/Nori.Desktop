using System.Text.Json;
using System.Globalization;
using Avalonia.Threading;

namespace Nori.Desktop.Settings;

/// <summary>AI 对话与 Embedding 设置页。</summary>
public sealed class AiSettingsPage : SettingsPageBase
{
	private static readonly IReadOnlyList<SettingsOption> ProviderOptions =
	[
		new("openai", new("OpenAI Chat Completions", "OpenAI Chat Completions")),
		new("openai_responses", new("OpenAI Responses", "OpenAI Responses")),
		new("anthropic", new("Anthropic", "Anthropic")),
		new("google", new("Google Gemini", "Google Gemini")),
	];

	private readonly SettingsFieldViewModel _provider;
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
	private bool _actionBusy;

	/// <summary>创建 AI 设置页。</summary>
	public AiSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "ai", "core", new("AI 大脑", "AI brain"), new("配置对话、模型和知识库向量服务。", "Configure chat, model and knowledge embedding providers."), lifetimeToken)
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

		AddAction(chat, "fetchModels", new("获取模型", "Fetch models"), new("使用当前服务商列出可用模型。", "List models from the selected provider."), new SettingsCommand(_ => _ = FetchModelsAsync()));
		AddAction(chat, "testChat", new("测试对话连接", "Test chat connection"), new("发送一次最小连接测试。", "Run a minimal connection test."), new SettingsCommand(_ => _ = TestChatAsync()));

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
		AddAction(embedding, "testEmbedding", new("测试向量连接", "Test embedding connection"), new("检查 Embedding 地址、密钥和模型。", "Check the embedding endpoint, key and model."), new SettingsCommand(_ => _ = TestEmbeddingAsync()));
	}

	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		UpdateModelOptions();
	}

	private void UpdateModelOptions()
	{
		IEnumerable<string> values = new[] {_model.Text}.Concat(_models).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal);
		_modelPicker.SetOptions(values.Select(value => new SettingsOption(value, new(value, value))));
		_modelPicker.IsVisible = _models.Count > 0;
	}

	private async Task<JsonElement> SaveProviderAsync(string provider, CancellationToken cancellationToken)
	{
		string address = await Dispatcher.UIThread.InvokeAsync(() => DefaultBaseUrl(provider, _baseUrl.Text));
		JsonElement result = await ExecuteAsync("settings_update_ai_providers", new {chat = new {provider, baseUrl = address}}, cancellationToken).ConfigureAwait(false);
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_models.Clear();
			UpdateModelOptions();
		});
		return result;
	}

	/// <summary>更换协议时只替换已知默认地址，保留自定义代理。</summary>
	internal static string DefaultBaseUrl(string provider, string current)
	{
		Dictionary<string, string> defaults = new(StringComparer.Ordinal)
		{
			["openai"] = "https://api.openai.com/v1",
			["openai_responses"] = "https://api.openai.com/v1",
			["anthropic"] = "https://api.anthropic.com/v1",
			["google"] = "https://generativelanguage.googleapis.com/v1beta",
		};
		string address = current.Trim();
		return (address.Length == 0 || defaults.Values.Contains(address, StringComparer.OrdinalIgnoreCase))
			&& defaults.TryGetValue(provider, out string? next) ? next : address;
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
		if (_actionBusy) return;
		SetActionsBusy(true);
		try
		{
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
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
			if (_models.Count == 0) SetStatus(Text("没有找到可用模型。", "No models were returned."));
			else SetStatus(Text($"已找到 {_models.Count} 个模型，可在列表中选择。", $"{_models.Count} models available to select."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally { SetActionsBusy(false); }
	}

	private void SetActionsBusy(bool busy)
	{
		_actionBusy = busy;
		foreach (SettingsFieldViewModel field in Sections.SelectMany(section => section.Fields))
			if (field.EditorKind == SettingsEditorKind.Action) field.IsReadOnly = busy;
		if (busy) SetStatus(Text("正在连接服务…", "Connecting to the provider…"));
	}

	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;

	private async Task TestChatAsync()
	{
		if (_actionBusy) return;
		SetActionsBusy(true);
		try
		{
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
			ApplySnapshot(await Service.GetSnapshotAsync(LifetimeToken).ConfigureAwait(true));
			JsonElement result = await ExecuteAsync("ai_test_connection", new
			{
				target = "chat",
				provider = _provider.Selected,
				baseUrl = _baseUrl.Text.Trim(),
				model = _model.Text.Trim(),
			}, LifetimeToken).ConfigureAwait(true);
			SetStatus(ConnectionStatus(result));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally { SetActionsBusy(false); }
	}

	private async Task TestEmbeddingAsync()
	{
		if (_actionBusy) return;
		SetActionsBusy(true);
		try
		{
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
			ApplySnapshot(await Service.GetSnapshotAsync(LifetimeToken).ConfigureAwait(true));
			JsonElement result = await ExecuteAsync("ai_test_connection", new
			{
				target = "embedding",
				baseUrl = _embeddingBaseUrl.Text.Trim(),
				model = _embeddingModel.Text.Trim(),
				dimensions = NormalizeDimensions(_embeddingDimensions.Text),
			}, LifetimeToken).ConfigureAwait(true);
			SetStatus(ConnectionStatus(result));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally { SetActionsBusy(false); }
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
