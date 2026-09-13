using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Configuration;

/// <summary>
/// 配置读写。
///
/// 敏感配置统一使用 nsec2 AES-256-GCM 保存, 配置键作为 AAD 绑定密文用途。
/// nsec1 与 Windows 旧 enc:dpapi: 只读兼容并在成功读取后惰性迁移。任何密钥库
/// 或加密失败都会中止写入, 绝不把明文作为回退值写入数据库。
/// </summary>
public sealed class ConfigStore(NoriDatabase database, ISecretKeyStore? keyStore = null)
{
	private readonly ISecretKeyStore _keyStore = keyStore ?? new SecretKeyStore();
	private readonly NoriDatabase _database = database;
	private readonly ConcurrentDictionary<string, SecretIssue> _secretIssues = new(StringComparer.Ordinal);

	/// <summary>配置键: 配置结构版本。</summary>
	public const string KeyConfigSchemaVersion = "config_schema_version";

	/// <summary>配置键: 首次启动 (数据库创建) 时间。</summary>
	public const string KeyInstalledAt = "installed_at";

	/// <summary>配置键: 首次初始化完成时间。</summary>
	public const string KeyInitializedAt = "initialized_at";

	/// <summary>配置键: 应用版本 (首次安装时的版本)。</summary>
	public const string KeyAppVersion = "app_version";

	/// <summary>配置键: 界面语言。</summary>
	public const string KeyLanguage = "language";

	/// <summary>配置键: 旧版界面语言 (仅用于 v1 → v2 迁移)。</summary>
	public const string LegacyKeyLanguage = "app_language";

	/// <summary>配置键: 伴侣模型。</summary>
	/// <summary>
	/// 文件工具的工作目录（绝对路径）。空串 = 整族文件工具不注册。
	///
	/// 默认空而不是给一个「用户目录」之类的值：那等于替用户做了一个他没同意过的决定。
	/// 配这个目录本身就是用户那次同意，所以目录内的读取不再逐次弹框（写入仍然每次都问）。
	/// </summary>
	public const string KeyWorkspaceRoot = "workspace_root";

	/// <summary>
	/// 一轮对话里最多连续调用多少次工具。
	///
	/// 原先写死 5，而一个「先搜、再读、再改、再验」的任务在第五轮刚好卡在验证之前 —— 她做了
	/// 一半就停下，对用户表现成「没做完也没说为什么」。放宽不增加简单对话的开销：模型不调
	/// 工具时循环自己就结束了，上限只决定它最多能走多远。
	/// </summary>
	public const string KeyAgentMaxToolIterations = "agent_max_tool_iterations";

	public const string KeySelectedModel = "selected_model";

	/// <summary>配置键: 首次初始化是否已完成。</summary>
	public const string KeyFirstRunCompleted = "first_run_completed";

	/// <summary>配置键: 明确的遥测同意状态。</summary>
	public const string KeyTelemetryConsent = "telemetry_consent";

	/// <summary>旧版布尔遥测开关, 只用于迁移和兼容读取。</summary>
	public const string KeyTelemetryEnabled = "telemetry_enabled";

	/// <summary>MCP stdio 环境变量的独立敏感配置键前缀。</summary>
	public const string McpEnvironmentKeyPrefix = "mcp_server_env_";

	/// <summary>配置键: 伴侣窗口 X 坐标。</summary>
	public const string KeyPetWindowX = "pet_window_x";

	/// <summary>配置键: 伴侣窗口 Y 坐标。</summary>
	public const string KeyPetWindowY = "pet_window_y";

	/// <summary>配置键: 全局音频音量 (0.0 ~ 1.0)。</summary>
	public const string KeyAudioVolume = "audio_volume";

	/// <summary>配置键: 自动化总开关。</summary>
	public const string KeyAutomationEnabled = "automation_enabled";

	/// <summary>配置键: 自动化鼠标能力显式授权。</summary>
	public const string KeyAutomationAllowPointer = "automation_allow_pointer";

	/// <summary>配置键: 自动化键盘能力显式授权。</summary>
	public const string KeyAutomationAllowKeyboard = "automation_allow_keyboard";

	/// <summary>配置键: 自动化滚轮能力显式授权。</summary>
	public const string KeyAutomationAllowScroll = "automation_allow_scroll";

	/// <summary>配置键: 浏览器自动化显式开关。</summary>
	public const string KeyAutomationBrowserEnabled = "automation_browser_enabled";

	/// <summary>当前配置结构版本。</summary>
	public const long ConfigSchemaVersion = 3;

	/// <summary>没有版本记录的旧数据库所使用的最后旧版本。</summary>
	private const long LegacyConfigSchemaVersion = 1;

	/// <summary>默认伴侣模型。</summary>
	public const string DefaultModel = "arg-nori";

	/// <summary>判断某个配置键是否必须加密。</summary>
	public static bool IsSensitiveKey(string key) =>
		key.StartsWith(McpEnvironmentKeyPrefix, StringComparison.Ordinal) ||
		key.EndsWith("_api_key", StringComparison.OrdinalIgnoreCase) ||
		key.EndsWith("_secret", StringComparison.OrdinalIgnoreCase) ||
		key.EndsWith("_token", StringComparison.OrdinalIgnoreCase) ||
		key.EndsWith("_password", StringComparison.OrdinalIgnoreCase);

	/// <summary>读取配置, 敏感值无法解密时按未配置处理。</summary>
	public ConfigValue? Get(string key)
	{
		string stored = RawValue(key);
		if (stored.Length == 0 && !Exists(key)) return null;

		if (!IsSensitiveKey(key)) return ConfigValue.FromStorage(stored);
		SecretReadResult result = ReadSecretValue(key, stored, migrate: true);
		return result.IsConfigured ? ConfigValue.FromStorage(result.Value!) : null;
	}

	/// <summary>
	/// 写入配置。敏感字段先完成加密再执行 SQL, 密钥库不可用时原记录保持不变。
	/// </summary>
	public void Set(string key, ConfigValue value)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		string toStore = IsSensitiveKey(key) ? ProtectValue(key, value.ToStorage()) : value.ToStorage();

		_database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO config (key, value)
				VALUES ($key, $value)
				ON CONFLICT(key)
				DO UPDATE SET value = excluded.value
				""";
			command.Parameters.AddWithValue("$key", key);
			command.Parameters.AddWithValue("$value", toStore);
			command.ExecuteNonQuery();
		});

		if (IsSensitiveKey(key)) _secretIssues.TryRemove(key, out _);
	}

	/// <summary>删除配置, 返回是否真的删除了记录。</summary>
	public bool Delete(string key)
	{
		bool deleted = _database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "DELETE FROM config WHERE key = $key";
			command.Parameters.AddWithValue("$key", key);
			return command.ExecuteNonQuery() > 0;
		});
		if (deleted) _secretIssues.TryRemove(key, out _);
		return deleted;
	}

	/// <summary>判断配置是否存在。</summary>
	public bool Exists(string key) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM config WHERE key = $key";
		command.Parameters.AddWithValue("$key", key);
		return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
	});

	/// <summary>
	/// 读取所有可用配置。无法解密的敏感值不会被伪装成一个普通字符串返回。
	/// </summary>
	public IReadOnlyList<KeyValuePair<string, ConfigValue>> GetAll()
	{
		List<(string Key, string Stored)> rows = _database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT key, value FROM config ORDER BY key";
			using SqliteDataReader reader = command.ExecuteReader();
			List<(string Key, string Stored)> values = [];
			while (reader.Read()) values.Add((reader.GetString(0), reader.GetString(1)));
			return values;
		});

		List<KeyValuePair<string, ConfigValue>> result = [];
		foreach ((string key, string stored) in rows)
		{
			if (IsSensitiveKey(key))
			{
				SecretReadResult secret = ReadSecretValue(key, stored, migrate: true);
				if (!secret.IsConfigured) continue;
				result.Add(new KeyValuePair<string, ConfigValue>(key, ConfigValue.FromStorage(secret.Value!)));
			}
			else
			{
				result.Add(new KeyValuePair<string, ConfigValue>(key, ConfigValue.FromStorage(stored)));
			}
		}
		return result;
	}

	/// <summary>读取一个敏感配置, 不返回任何替代明文或密文。</summary>
	public SecretReadResult ReadSecret(string key)
	{
		if (!IsSensitiveKey(key)) throw new ArgumentException("不是敏感配置键", nameof(key));
		string stored = RawValue(key);
		return stored.Length == 0 && !Exists(key)
			? new SecretReadResult(null, SecretIssueCategory.None)
			: ReadSecretValue(key, stored, migrate: true);
	}

	/// <summary>判断敏感配置当前是否真正可用。</summary>
	public bool IsSecretConfigured(string key) => ReadSecret(key).IsConfigured;

	/// <summary>读取某个敏感配置的问题分类, 不包含值。</summary>
	public SecretIssue? GetSecretIssue(string key) => _secretIssues.TryGetValue(key, out SecretIssue? issue) ? issue : null;

	/// <summary>读取全部敏感配置问题摘要, 不包含值。</summary>
	public IReadOnlyList<SecretIssue> GetSecretIssues() => _secretIssues.Values
		.OrderBy(issue => issue.Key, StringComparer.Ordinal)
		.ToArray();

	/// <summary>供其它核心服务记录一个不含值的敏感配置问题。</summary>
	public void RecordSecretIssue(string key, SecretIssueCategory category)
	{
		if (category == SecretIssueCategory.None)
		{
			_secretIssues.TryRemove(key, out _);
			return;
		}
		_secretIssues[key] = new SecretIssue(key, category);
	}

	/// <summary>
	/// 在已有事务中写入配置。
	/// </summary>
	private void SetInTransaction(
		SqliteConnection connection,
		SqliteTransaction transaction,
		string key,
		ConfigValue value,
		bool insertOnly = false)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = insertOnly
			? "INSERT OR IGNORE INTO config (key, value) VALUES ($key, $value)"
			: "INSERT INTO config (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
		command.Parameters.AddWithValue("$key", key);
		command.Parameters.AddWithValue("$value", IsSensitiveKey(key) ? ProtectValue(key, value.ToStorage()) : value.ToStorage());
		command.ExecuteNonQuery();
	}

	/// <summary>读取未经解密的原始存储值 (迁移与安全测试用)。</summary>
	public string RawValue(string key) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT value FROM config WHERE key = $key";
		command.Parameters.AddWithValue("$key", key);
		return command.ExecuteScalar() as string ?? "";
	});

	/// <summary>读取字符串配置, 缺失/类型不符时返回 fallback。</summary>
	public string GetStringOr(string key, string fallback) => ConfigValue.AsStringOr(Get(key), fallback);

	/// <summary>读取整型配置并夹紧到 [min, max]; 无效或缺省时返回 fallback。</summary>
	public int GetClampedInt(string key, int fallback, int min, int max) =>
		int.TryParse(GetStringOr(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
			? Math.Clamp(value, min, max)
			: fallback;

	/// <summary>读取浮点配置并夹紧到 [min, max]; 无效或缺省时返回 fallback。</summary>
	public double GetClampedDouble(string key, double fallback, double min, double max) =>
		double.TryParse(GetStringOr(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? Math.Clamp(value, min, max)
			: fallback;

	/// <summary>
	/// 读取布尔配置, 兼容历史上可能写入的 0/1 与 true/false 字符串。
	/// 旧遥测键会投影明确同意状态, 但不会把 unset 当成已同意。
	/// </summary>
	public bool GetBoolOr(string key, bool fallback)
	{
		if (key == KeyTelemetryEnabled) return GetTelemetryConsent() == TelemetryConsent.Granted;
		ConfigValue? value = Get(key);
		if (value is ConfigValue.Boolean boolean) return boolean.Value;
		string raw = ConfigValue.AsStringOr(value, "");
		return raw switch
		{
			"1" => true,
			"0" => false,
			_ when bool.TryParse(raw, out bool parsed) => parsed,
			_ => fallback,
		};
	}

	/// <summary>读取明确的遥测同意状态; 非法或缺失值都 fail-closed 为 unset。</summary>
	public TelemetryConsent GetTelemetryConsent()
	{
		ConfigValue? value = Get(KeyTelemetryConsent);
		string raw = ConfigValue.AsStringOr(value, "");
		if (ConfigValidation.TryParseTelemetryConsent(raw, out TelemetryConsent consent)) return consent;

		// 迁移尚未运行或旧数据库被直接读取时仍按旧语义兼容: false=denied, true=unset.
		string legacy = RawValue(KeyTelemetryEnabled);
		if (legacy is "0" || legacy.Equals("false", StringComparison.OrdinalIgnoreCase)) return TelemetryConsent.Denied;
		if (legacy is "1" || legacy.Equals("true", StringComparison.OrdinalIgnoreCase)) return TelemetryConsent.Unset;
		return TelemetryConsent.Unset;
	}

	/// <summary>保存明确的遥测同意状态。</summary>
	public void SetTelemetryConsent(TelemetryConsent consent) =>
		Set(KeyTelemetryConsent, new ConfigValue.Text(ConfigValidation.TelemetryConsentStorage(consent)));

	/// <summary>
	/// 初始化默认配置: 只补缺失项, 不覆盖用户已有配置。
	/// </summary>
	public void InitDefaults(string appVersion)
	{
		// 新数据库没有旧配置可迁移；跳过空库迁移，避免为初始化空库生成无意义备份。
		if (GetAll().Count > 0) EnsureSchemaVersion();
		_database.Locked(connection =>
		{
			(string Key, ConfigValue Value)[] defaults =
			[
				(KeyConfigSchemaVersion, new ConfigValue.Integer(ConfigSchemaVersion)),
				(KeyAppVersion, new ConfigValue.Text(appVersion)),
				(KeyInstalledAt, new ConfigValue.Text(Now())),
				(KeyLanguage, new ConfigValue.Text(SystemLanguage())),
				(KeySelectedModel, new ConfigValue.Text(DefaultModel)),
				(KeyFirstRunCompleted, new ConfigValue.Boolean(false)),
				(KeyTelemetryConsent, new ConfigValue.Text(ConfigValidation.TelemetryConsentStorage(TelemetryConsent.Unset))),
				("memory_enabled", new ConfigValue.Boolean(true)),
				("memory_reflection_enabled", new ConfigValue.Boolean(true)),
				("memory_reflection_rounds", new ConfigValue.Integer(8)),
				("memory_reflection_min_chars", new ConfigValue.Integer(2500)),
				("memory_recall_top_k", new ConfigValue.Integer(6)),
				("memory_keyword_top_k", new ConfigValue.Integer(20)),
				("memory_vector_top_k", new ConfigValue.Integer(20)),
				("memory_rrf_k", new ConfigValue.Integer(60)),
				("memory_min_similarity", new ConfigValue.Text("0.25")),
				("memory_decay_enabled", new ConfigValue.Boolean(true)),
				("memory_archive_enabled", new ConfigValue.Boolean(true)),
				("memory_source_retention_threshold", new ConfigValue.Text("0.75")),
				("memory_archive_threshold", new ConfigValue.Text("0.15")),
				("memory_knowledge_enabled", new ConfigValue.Boolean(true)),
				("memory_knowledge_watch", new ConfigValue.Boolean(true)),
				("memory_debug_retrieval", new ConfigValue.Boolean(false)),
				// 对话自动朗读: 默认关闭, 用户手动开启后持久化。前后端读缺省值保持一致 (false)。
				("tts_auto_play", new ConfigValue.Boolean(false)),
				(KeyAutomationEnabled, new ConfigValue.Boolean(false)),
				(KeyAutomationAllowPointer, new ConfigValue.Boolean(false)),
				(KeyAutomationAllowKeyboard, new ConfigValue.Boolean(false)),
				(KeyAutomationAllowScroll, new ConfigValue.Boolean(false)),
				(KeyAutomationBrowserEnabled, new ConfigValue.Boolean(false)),
			];
			foreach ((string key, ConfigValue value) in defaults)
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "INSERT OR IGNORE INTO config (key, value) VALUES ($key, $value)";
				command.Parameters.AddWithValue("$key", key);
				command.Parameters.AddWithValue("$value", value.ToStorage());
				command.ExecuteNonQuery();
			}
		});
	}

	/// <summary>
	/// 检查配置结构版本。低版本逐级迁移, 高版本直接拒绝。
	/// </summary>
	public void EnsureSchemaVersion()
	{
		long current = ReadSchemaVersion();
		if (current > ConfigSchemaVersion)
		{
			throw new InvalidOperationException($"配置数据库版本 {current} 高于当前应用支持版本 {ConfigSchemaVersion}, 请升级应用");
		}
		if (current < ConfigSchemaVersion)
		{
			_database.EnsureMigrationBackup();
			MigrateSchema(current, ConfigSchemaVersion);
		}
	}

	/// <summary>数据库结构迁移: v1 语言键, v2 遥测布尔键迁移为三态同意。</summary>
	private void MigrateSchema(long from, long to) => _database.Locked(connection =>
	{
		using SqliteTransaction transaction = connection.BeginTransaction();
		try
		{
			long version = from;
			while (version < to)
			{
				switch (version)
				{
					case LegacyConfigSchemaVersion:
						MigrateLanguage(connection, transaction);
						break;
					case 2:
						MigrateTelemetryConsent(connection, transaction);
						break;
					default:
						throw new InvalidOperationException($"不支持的配置数据库版本: {version}");
				}
				version++;
				SetSchemaVersion(connection, transaction, version);
			}
			transaction.Commit();
		}
		catch
		{
			try { transaction.Rollback(); }
			catch { /* 保留原始迁移异常。 */ }
			throw;
		}
	});

	/// <summary>规范化语言键: language 已存在时优先保留, 否则回填 app_language。</summary>
	private static void MigrateLanguage(SqliteConnection connection, SqliteTransaction transaction)
	{
		using SqliteCommand copy = connection.CreateCommand();
		copy.Transaction = transaction;
		copy.CommandText = """
			INSERT INTO config (key, value)
			SELECT $language, legacy.value
			FROM config AS legacy
			WHERE legacy.key = $legacy
				AND NOT EXISTS (SELECT 1 FROM config WHERE key = $language);
			""";
		copy.Parameters.AddWithValue("$language", KeyLanguage);
		copy.Parameters.AddWithValue("$legacy", LegacyKeyLanguage);
		copy.ExecuteNonQuery();

		using SqliteCommand delete = connection.CreateCommand();
		delete.Transaction = transaction;
		delete.CommandText = "DELETE FROM config WHERE key = $legacy";
		delete.Parameters.AddWithValue("$legacy", LegacyKeyLanguage);
		delete.ExecuteNonQuery();
	}

	/// <summary>旧版 false 迁移为 denied, 历史 true 迁移为 unset, 不自动开始上报。</summary>
	private static void MigrateTelemetryConsent(SqliteConnection connection, SqliteTransaction transaction)
	{
		string? existing = ReadValue(connection, transaction, KeyTelemetryConsent);
		if (ConfigValidation.TryParseTelemetryConsent(existing, out _))
		{
			DeleteValue(connection, transaction, KeyTelemetryEnabled);
			return;
		}

		string? legacy = ReadValue(connection, transaction, KeyTelemetryEnabled);
		TelemetryConsent consent = legacy is "0" || legacy?.Equals("false", StringComparison.OrdinalIgnoreCase) == true
			? TelemetryConsent.Denied
			: TelemetryConsent.Unset;
		SetValue(connection, transaction, KeyTelemetryConsent, ConfigValidation.TelemetryConsentStorage(consent));
		DeleteValue(connection, transaction, KeyTelemetryEnabled);
	}

	private static string? ReadValue(SqliteConnection connection, SqliteTransaction transaction, string key)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT value FROM config WHERE key = $key";
		command.Parameters.AddWithValue("$key", key);
		return command.ExecuteScalar() as string;
	}

	private static void DeleteValue(SqliteConnection connection, SqliteTransaction transaction, string key)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM config WHERE key = $key";
		command.Parameters.AddWithValue("$key", key);
		command.ExecuteNonQuery();
	}

	private static void SetValue(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO config (key, value) VALUES ($key, $value)
			ON CONFLICT(key) DO UPDATE SET value = excluded.value;
			""";
		command.Parameters.AddWithValue("$key", key);
		command.Parameters.AddWithValue("$value", value);
		command.ExecuteNonQuery();
	}

	/// <summary>在迁移事务中写入配置版本。</summary>
	private static void SetSchemaVersion(SqliteConnection connection, SqliteTransaction transaction, long version) =>
		SetValue(connection, transaction, KeyConfigSchemaVersion, version.ToString(CultureInfo.InvariantCulture));

	/// <summary>读取配置结构版本. 没有版本记录的数据库按 v1 处理。</summary>
	private long ReadSchemaVersion() => Get(KeyConfigSchemaVersion) switch
	{
		null => LegacyConfigSchemaVersion,
		ConfigValue.Integer integer => integer.Value,
		ConfigValue.Boolean boolean => boolean.Value ? 1 : 0,
		ConfigValue.Text text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
		_ => ConfigSchemaVersion,
	};

	/// <summary>敏感值加密; 任意失败都会抛出, 绝不返回明文。</summary>
	private string ProtectValue(string key, string plainText)
	{
		if (!IsSensitiveKey(key) || string.IsNullOrEmpty(plainText)) return plainText;
		try
		{
			byte[] masterKey = _keyStore.LoadOrCreate();
			if (masterKey.Length != SecretKeyStore.KeySize)
				throw new SecretKeyStoreException("平台主密钥长度无效, 拒绝写入敏感配置");
			return SecretProtector.ProtectV2(masterKey, key, plainText);
		}
		catch (SecretKeyStoreException)
		{
			throw;
		}
		catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			throw new SecretKeyStoreException($"敏感配置 {key} 无法使用平台密钥库, 已拒绝写入", exception);
		}
	}

	/// <summary>解密一个原始敏感值, 并在成功时尝试升级到 nsec2。</summary>
	private SecretReadResult ReadSecretValue(string key, string stored, bool migrate)
	{
		if (string.IsNullOrEmpty(stored)) return new SecretReadResult(null, SecretIssueCategory.None);

		if (SecretProtector.IsNsec2(stored) || SecretProtector.IsNsec1(stored))
		{
			byte[] masterKey;
			try { masterKey = _keyStore.LoadOrCreate(); }
			catch (Exception exception) when (exception is SecretKeyStoreException or CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
			{
				RecordSecretIssue(key, SecretIssueCategory.KeyStoreUnavailable);
				return new SecretReadResult(null, SecretIssueCategory.KeyStoreUnavailable);
			}

			bool valid = SecretProtector.IsNsec2(stored)
				? SecretProtector.TryUnprotectV2(masterKey, key, stored, out string plain)
				: SecretProtector.TryUnprotectV1(masterKey, stored, out plain);
			if (!valid)
			{
				RecordSecretIssue(key, SecretIssueCategory.CorruptCiphertext);
				return new SecretReadResult(null, SecretIssueCategory.CorruptCiphertext);
			}

			SecretIssueCategory category = SecretProtector.IsNsec1(stored)
				? SecretIssueCategory.LegacyNsec1
				: SecretIssueCategory.None;
			if (migrate && category != SecretIssueCategory.None) TryMigrate(key, plain);
			if (category == SecretIssueCategory.None) _secretIssues.TryRemove(key, out _);
			return new SecretReadResult(plain, category);
		}

		if (SecretProtector.IsLegacyDpapi(stored))
		{
			if (!OperatingSystem.IsWindows())
			{
				RecordSecretIssue(key, SecretIssueCategory.LegacyUnsupported);
				return new SecretReadResult(null, SecretIssueCategory.LegacyUnsupported);
			}
			if (!TryUnprotectLegacyDpapi(stored, out string plain))
			{
				RecordSecretIssue(key, SecretIssueCategory.CorruptCiphertext);
				return new SecretReadResult(null, SecretIssueCategory.CorruptCiphertext);
			}
			if (migrate) TryMigrate(key, plain);
			return new SecretReadResult(plain, SecretIssueCategory.LegacyDpapi);
		}

		// 早期错误回退曾把敏感值明文写入数据库。保留一次可用读取并立即尝试加密迁移,
		// 但绝不再把这类值原样作为新的存储结果写回。
		if (migrate) TryMigrate(key, stored);
		return new SecretReadResult(stored, SecretIssueCategory.LegacyPlaintext);
	}

	private void TryMigrate(string key, string plain)
	{
		try
		{
			string encrypted = ProtectValue(key, plain);
			WriteRaw(key, encrypted);
			_secretIssues.TryRemove(key, out _);
		}
		catch (Exception exception) when (exception is SecretKeyStoreException or CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			RecordSecretIssue(key, SecretIssueCategory.KeyStoreUnavailable);
		}
	}

	private void WriteRaw(string key, string value) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO config (key, value) VALUES ($key, $value)
			ON CONFLICT(key) DO UPDATE SET value = excluded.value;
			""";
		command.Parameters.AddWithValue("$key", key);
		command.Parameters.AddWithValue("$value", value);
		command.ExecuteNonQuery();
	});

	private static bool TryUnprotectLegacyDpapi(string stored, out string plain)
	{
		plain = string.Empty;
		if (!OperatingSystem.IsWindows()) return false;
		try
		{
			byte[] encrypted = Convert.FromBase64String(stored[SecretProtector.LegacyDpapiPrefix.Length..]);
			byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
			plain = Encoding.UTF8.GetString(decrypted);
			return true;
		}
		catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
		{
			return false;
		}
	}

	/// <summary>判断是否首次启动。</summary>
	public bool IsFirstRun() => Get(KeyFirstRunCompleted) switch
	{
		null => true,
		ConfigValue.Boolean boolean => !boolean.Value,
		ConfigValue.Integer integer => integer.Value == 0,
		ConfigValue.Text text => text.Value == "0" || text.Value.Equals("false", StringComparison.OrdinalIgnoreCase),
		_ => false,
	};

	/// <summary>标记首次启动完成。</summary>
	public void MarkFirstRunCompleted() => Set(KeyFirstRunCompleted, new ConfigValue.Boolean(true));

	/// <summary>
	/// 原子提交首次运行向导的最终选择。
	///
	/// 模型、遥测同意、首次运行标记与初始化时间必须一起落盘，避免进程在
	/// 首次运行标记已经写入后崩溃，下一次启动却缺少必要配置。
	/// </summary>
	public void CompleteFirstRun(string modelId, bool telemetryEnabled)
	{
		if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("模型 ID 不能为空", nameof(modelId));

		_database.Locked(connection =>
		{
			using SqliteTransaction transaction = connection.BeginTransaction();
			try
			{
				SetInTransaction(connection, transaction, KeySelectedModel, new ConfigValue.Text(modelId.Trim()));
				TelemetryConsent consent = telemetryEnabled ? TelemetryConsent.Granted : TelemetryConsent.Denied;
				SetInTransaction(connection, transaction, KeyTelemetryConsent,
					new ConfigValue.Text(ConfigValidation.TelemetryConsentStorage(consent)));
				SetInTransaction(connection, transaction, KeyFirstRunCompleted, new ConfigValue.Boolean(true));
				SetInTransaction(connection, transaction, KeyInitializedAt, new ConfigValue.Text(Now()), insertOnly: true);
				transaction.Commit();
			}
			catch
			{
				try { transaction.Rollback(); } catch { }
				throw;
			}
		});
	}

	/// <summary>记录首次初始化完成时间 (只写一次)。</summary>
	public void MarkInitialized()
	{
		if (!Exists(KeyInitializedAt)) Set(KeyInitializedAt, new ConfigValue.Text(Now()));
	}

	/// <summary>首次初始化配置快照, 对应前端 invoke("get_init_config")。</summary>
	public InitConfig GetInitConfig()
	{
		string? initializedAt = Get(KeyInitializedAt) is ConfigValue.Text text && text.Value.Length > 0 ? text.Value : null;
		return new InitConfig
		{
			ConfigSchemaVersion = ReadSchemaVersion(),
			AppVersion = GetStringOr(KeyAppVersion, "unknown"),
			InstalledAt = GetStringOr(KeyInstalledAt, ""),
			InitializedAt = initializedAt,
			Language = GetStringOr(KeyLanguage, SystemLanguage()),
			SelectedModel = GetStringOr(KeySelectedModel, DefaultModel),
		};
	}

	/// <summary>当前本地时间, 形如 2026-01-01 12:00:00。</summary>
	private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

	/// <summary>系统语言, 获取失败时回退 zh-CN。</summary>
	public static string SystemLanguage()
	{
		string name = CultureInfo.CurrentUICulture.Name;
		return string.IsNullOrEmpty(name) ? "zh-CN" : name;
	}
}
