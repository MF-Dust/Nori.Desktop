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
	/// 会话 id 是跟着哪一台服务端、哪一把密钥建出来的。
	///
	/// 存的是指纹（SHA-256 十六进制）而不是原值：这一列不敏感、不加密，把密钥原样写进去
	/// 等于绕开 `_api_key` 那套加密存储。
	/// </summary>
	public const string KeySessionOwner = "luolicore_session_owner";

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

	/// <summary>
	/// 一台服务端 + 一把密钥的身份指纹。换了任意一项，之前建的会话就不再属于这里。
	/// </summary>
	private static string Fingerprint(string baseUrl, string apiKey)
	{
		if (baseUrl.Length == 0 || apiKey.Length == 0) return string.Empty;
		// 换行分隔，免得 ("http://a", "b") 与 ("http://ab", "") 这类拼接出同一个串。
		byte[] digest = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(baseUrl + "\n" + apiKey));
		return Convert.ToHexString(digest);
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

		// 会话 id 绑在建它的那台服务端与那把密钥上。
		//
		// 换了 BaseUrl 或换了密钥之后，存量的会话 id 属于**另一个**服务端 —— 继续拿它发消息
		// 只会一直 404，而且这个失败看起来像「服务端坏了」，不像「你改了配置」。指纹对不上
		// 就当作没有会话，下一轮自然新建一个，旧的那条留在原服务端上不动。
		//
		// 环境变量直接给了会话 id 的情形不受此限：那是运维显式指定的，他知道自己在干什么。
		string storedSession = _config.GetStringOr(KeySessionId, "").Trim();
		string owner = _config.GetStringOr(KeySessionOwner, "").Trim();
		string current = Fingerprint(baseUrl, apiKey);
		if (storedSession.Length > 0 && owner.Length > 0 && owner != current) storedSession = "";

		return new LuoLiCoreSettings
		{
			Enabled = enabled,
			BaseUrl = baseUrl,
			ApiKey = apiKey,
			SessionId = storedSession.Length > 0 ? storedSession : Env(EnvSessionId),
			ModelAlias = ReadOr(KeyModelAlias, EnvModelAlias),
		};
	}

	/// <summary>
	/// 首次对话建好会话后写回，下次接着用同一个。
	///
	/// 同时记下它属于哪台服务端、哪把密钥（指纹）。没有这一项的话，运维换了地址或换了密钥
	/// 之后旧 id 仍然会被拿去用，表现成持续 404。
	/// </summary>
	public void SaveSessionId(string sessionId)
	{
		string trimmed = (sessionId ?? string.Empty).Trim();
		if (trimmed.Length == 0) return;
		_config.Set(KeySessionId, new ConfigValue.Text(trimmed));

		LuoLiCoreSettings current = Read();
		_config.Set(
			KeySessionOwner,
			new ConfigValue.Text(Fingerprint(current.BaseUrl, current.ApiKey)));
	}
}
