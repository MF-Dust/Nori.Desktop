using Nori.Core.Configuration;

namespace Nori.Core.Chat.LuoLiCore;

/// <summary>
/// 把对话交给 LuoLiCore 的配置。
///
/// 单开一节而不是往 <see cref="AiChatSettings"/> 里加字段：那条记录被设置页、校验、
/// 连接测试与多处测试共用，为一个可选后端改它的形状会波及一大片；而这一节和
/// <c>AiEmbeddingSettings</c> 一样，是与聊天配置并列的独立运行时配置。
///
/// 未启用时本节完全不参与对话链路，桌宠照旧走本机 agent。
/// </summary>
public sealed record LuoLiCoreSettings
{
	/// <summary>是否把对话交给 LuoLiCore。默认关闭。</summary>
	public required bool Enabled { get; init; }

	/// <summary>服务根地址，例如 <c>http://127.0.0.1:3000</c>。</summary>
	public required string BaseUrl { get; init; }

	/// <summary>`sk-` 开头的来源密钥。</summary>
	public required string ApiKey { get; init; }

	/// <summary>
	/// 会话 id。留空表示还没建过，首次对话时建一个并写回。
	///
	/// 固定一个会话而不是每次新建：记忆与上下文都挂在会话上，每次新建等于她每次都失忆。
	/// </summary>
	public required string SessionId { get; init; }

	/// <summary>模型别名。留空用服务端会话上的默认值。</summary>
	public required string ModelAlias { get; init; }

	/// <summary>配置齐了才可能启用。地址与密钥缺一不可，会话 id 可以为空（首次会建）。</summary>
	public bool IsConfigured => BaseUrl.Length > 0 && ApiKey.Length > 0;

	/// <summary>真正生效的判据。设置页可以先填一半，不该因此就把对话切过去。</summary>
	public bool IsActive => Enabled && IsConfigured;

	/// <summary>转成客户端参数。</summary>
	public LuoLiCoreSdkOptions ToOptions() => new()
	{
		BaseUrl = BaseUrl,
		ApiKey = ApiKey,
		ModelAlias = ModelAlias.Length == 0 ? null : ModelAlias,
	};
}

/// <summary>LuoLiCore 配置的读写。</summary>
/// <param name="config">配置库。</param>
/// <param name="environment">
/// 环境变量读取入口。默认读进程环境；测试注入它是为了不去改进程级状态 —— 那会串到
/// 并行跑的其他测试里，表现成偶发失败。
/// </param>
public sealed class LuoLiCoreSettingsStore(ConfigStore config, Func<string, string?>? environment = null)
{
	public const string KeyEnabled = "luolicore_enabled";
	public const string KeyBaseUrl = "luolicore_base_url";
	public const string KeyApiKey = "luolicore_api_key";
	public const string KeySessionId = "luolicore_session_id";
	public const string KeyModelAlias = "luolicore_model_alias";

	/// <summary>
	/// 环境变量兜底。
	///
	/// 原生 UI 迁移期间还没有填这几项的地方，而密钥本来就该走环境变量而不是写进文件。
	/// **配置优先、环境变量兜底**：将来设置页写了值就以设置页为准，不会出现「界面上改了
	/// 却不生效」这种查起来很贵的错位。
	/// </summary>
	public const string EnvEnabled = "NORI_LUOLICORE_ENABLED";
	public const string EnvBaseUrl = "NORI_LUOLICORE_BASE_URL";
	public const string EnvApiKey = "NORI_LUOLICORE_API_KEY";
	public const string EnvSessionId = "NORI_LUOLICORE_SESSION_ID";
	public const string EnvModelAlias = "NORI_LUOLICORE_MODEL_ALIAS";

	private readonly ConfigStore _config = config;
	private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

	private string Env(string name) => (_environment(name) ?? string.Empty).Trim();

	/// <summary>配置里有就用配置的；没有才看环境变量。</summary>
	private string ReadOr(string key, string envName)
	{
		string stored = _config.GetStringOr(key, "").Trim();
		return stored.Length > 0 ? stored : Env(envName);
	}

	public LuoLiCoreSettings Read()
	{
		// 开关没显式配过时，环境变量给了地址与密钥就视为打开 —— 迁移期没有界面可点，
		// 要求再单独设一个开关只会让人以为没生效。
		string envEnabled = Env(EnvEnabled);
		string baseUrl = ReadOr(KeyBaseUrl, EnvBaseUrl).TrimEnd('/');
		string apiKey = ReadOr(KeyApiKey, EnvApiKey);
		bool enabled = _config.GetBoolOr(
			KeyEnabled,
			envEnabled.Length > 0
				? envEnabled is "1" or "true" or "TRUE" or "True"
				: baseUrl.Length > 0 && apiKey.Length > 0);

		return new LuoLiCoreSettings
		{
			Enabled = enabled,
			BaseUrl = baseUrl,
			ApiKey = apiKey,
			SessionId = ReadOr(KeySessionId, EnvSessionId),
			ModelAlias = ReadOr(KeyModelAlias, EnvModelAlias),
		};
	}

	/// <summary>首次对话建好会话后写回，下次接着用同一个。</summary>
	public void SaveSessionId(string sessionId)
	{
		string trimmed = (sessionId ?? string.Empty).Trim();
		if (trimmed.Length == 0) return;
		_config.Set(KeySessionId, new ConfigValue.Text(trimmed));
	}
}
