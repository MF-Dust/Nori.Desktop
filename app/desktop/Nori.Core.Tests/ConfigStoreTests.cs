using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// 配置库读写与首次运行标记, 跑在临时 SQLite 文件上
/// </summary>
public class ConfigStoreTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-test-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	/// <summary>测试用主密钥: 固定值, 不碰用户真实数据目录</summary>
	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();
		public byte[] LoadOrCreate() => _key;
		public bool IsFileFallback => true;
	}

	public ConfigStoreTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("0.1.0");
		_config.EnsureSchemaVersion();
	}

	public void Dispose()
	{
		_database.Dispose();
		try
		{
			File.Delete(_path);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void 配置迁移会复用一次可恢复的迁移前备份()
	{
		_config.Set(ConfigStore.KeyConfigSchemaVersion, new ConfigValue.Integer(1));
		_config.Set(ConfigStore.LegacyKeyLanguage, new ConfigValue.Text("en-US"));

		_config.EnsureSchemaVersion();
		_config.EnsureSchemaVersion();

		string directory = Path.GetDirectoryName(_path)!;
		string pattern = $"{Path.GetFileName(_path)}.pre-migration-*.bak";
		string[] backups = Directory.GetFiles(directory, pattern);
		Assert.Single(backups);
		Assert.True(new FileInfo(backups[0]).Length > 0);

		using NoriDatabase backupDatabase = NoriDatabase.Open(backups[0]);
		ConfigStore backupConfig = new(backupDatabase, new FixedKeyStore());
		Assert.True(backupConfig.GetBoolOr(ConfigStore.KeyConfigSchemaVersion, false));
	}

	[Fact]
	public void BackgroundBlurDefaultsToEnabledAndPreservesSavedPreference()
	{
		Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
		_config.Set(ConfigStore.KeyBackgroundBlurEnabled, new ConfigValue.Boolean(false));
		_config.InitDefaults("0.2.0");
		Assert.False(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
		_config.Set(ConfigStore.KeyBackgroundBlurEnabled, new ConfigValue.Text("invalid"));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
	}

	[Fact]
	public void 默认配置在初始化后就位()
	{
		Assert.Equal("arg-nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		Assert.Equal("0.1.0", _config.GetStringOr(ConfigStore.KeyAppVersion, ""));
		Assert.NotEqual("", _config.GetStringOr(ConfigStore.KeyInstalledAt, ""));
		Assert.True(_config.Exists(ConfigStore.KeyLanguage));
		Assert.True(_config.GetQuickChatEnabled());
		Assert.False(_config.GetBoolOr(ConfigStore.KeyTelemetryEnabled, false));
		Assert.Equal(TelemetryConsent.Unset, _config.GetTelemetryConsent());
	}

	[Fact]
	public void 快捷聊天开关支持持久化并按默认开启()
	{
		Assert.True(_config.GetQuickChatEnabled());
		_config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(false));
		Assert.False(_config.GetQuickChatEnabled());
		_config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Text("true"));
		Assert.True(_config.GetQuickChatEnabled());
	}

	[Fact]
	public void 对话自动朗读默认关闭且重复初始化不覆盖用户开启()
	{
		// 初始化后就位为 false (与前端快照缺省一致, 避免"看着开了实际静音")
		Assert.False(_config.GetBoolOr("tts_auto_play", true));

		// 用户手动开启后, 重复初始化 (INSERT OR IGNORE) 不覆盖
		_config.Set("tts_auto_play", new ConfigValue.Text("true"));
		_config.InitDefaults("0.2.0");
		Assert.True(_config.GetBoolOr("tts_auto_play", false));
	}

	[Fact]
	public void 重复初始化不覆盖用户已有配置()
	{
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("nori"));
		_config.InitDefaults("0.2.0");
		Assert.Equal("nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		Assert.Equal("0.1.0", _config.GetStringOr(ConfigStore.KeyAppVersion, ""));
	}

	[Fact]
	public void 首次运行标记流程()
	{
		Assert.True(_config.IsFirstRun());
		_config.MarkFirstRunCompleted();
		Assert.False(_config.IsFirstRun());

		Assert.Null(_config.GetInitConfig().InitializedAt);
		_config.MarkInitialized();
		string? first = _config.GetInitConfig().InitializedAt;
		Assert.NotNull(first);
		// 再次调用不应改写时间
		_config.MarkInitialized();
		Assert.Equal(first, _config.GetInitConfig().InitializedAt);
	}

	[Fact]
	public void 首次运行完成会原子写入模型遥测和初始化标记()
	{
		_config.CompleteFirstRun("nori", false);

		Assert.False(_config.IsFirstRun());
		Assert.Equal("nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		Assert.False(_config.GetBoolOr(ConfigStore.KeyTelemetryEnabled, true));
		Assert.NotNull(_config.GetInitConfig().InitializedAt);
	}

	[Fact]
	public void 首次运行完成拒绝空模型()
	{
		Assert.Throws<ArgumentException>(() => _config.CompleteFirstRun("", true));
		Assert.True(_config.IsFirstRun());
	}

	[Fact]
	public void 读写删除与存在性()
	{
		Assert.Null(_config.Get("l2d_scale_arg-nori"));
		_config.Set("l2d_scale_arg-nori", new ConfigValue.Text("1.25"));
		Assert.Equal("1.25", Assert.IsType<ConfigValue.Text>(_config.Get("l2d_scale_arg-nori")).Value);
		Assert.True(_config.Exists("l2d_scale_arg-nori"));
		Assert.True(_config.Delete("l2d_scale_arg-nori"));
		Assert.False(_config.Delete("l2d_scale_arg-nori"));
		Assert.Null(_config.Get("l2d_scale_arg-nori"));
	}

	[Fact]
	public void 覆盖写入不会插入重复行()
	{
		_config.Set("k", new ConfigValue.Text("a"));
		_config.Set("k", new ConfigValue.Text("b"));
		Assert.Equal("b", _config.GetStringOr("k", ""));
		Assert.Single(_config.GetAll(), pair => pair.Key == "k");
	}

	[Fact]
	public void 全部配置按键排序()
	{
		_config.Set("zzz", new ConfigValue.Text("1"));
		_config.Set("aaa", new ConfigValue.Text("2"));
		List<string> keys = [.. _config.GetAll().Select(pair => pair.Key)];
		Assert.Equal([.. keys.Order(StringComparer.Ordinal)], keys);
	}

	[Fact]
	public void 数据库版本高于程序时拒绝启动()
	{
		_config.Set(ConfigStore.KeyConfigSchemaVersion, new ConfigValue.Integer(ConfigStore.ConfigSchemaVersion + 1));
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(_config.EnsureSchemaVersion);
		Assert.Contains("请升级应用", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void v1迁移会回填并删除旧语言键()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-language-migration-{Guid.NewGuid():N}.db");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			database.Locked(connection =>
			{
				using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
				command.CommandText = "INSERT INTO config (key, value) VALUES ('config_schema_version', '1'), ('app_language', 'en-US')";
				command.ExecuteNonQuery();
			});

			ConfigStore config = new(database);
			config.EnsureSchemaVersion();

			Assert.Equal("en-US", config.GetStringOr(ConfigStore.KeyLanguage, ""));
			Assert.False(config.Exists(ConfigStore.LegacyKeyLanguage));
			Assert.Equal(ConfigStore.ConfigSchemaVersion, Assert.IsType<ConfigValue.Integer>(config.Get(ConfigStore.KeyConfigSchemaVersion)).Value);
		}
		finally
		{
			TryDeleteDatabase(path);
		}
	}

	[Fact]
	public void v1迁移保留已存在的规范语言键()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-language-precedence-{Guid.NewGuid():N}.db");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			database.Locked(connection =>
			{
				using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
				command.CommandText = "INSERT INTO config (key, value) VALUES ('config_schema_version', '1'), ('language', 'zh-CN'), ('app_language', 'en-US')";
				command.ExecuteNonQuery();
			});

			ConfigStore config = new(database);
			config.EnsureSchemaVersion();

			Assert.Equal("zh-CN", config.GetStringOr(ConfigStore.KeyLanguage, ""));
			Assert.False(config.Exists(ConfigStore.LegacyKeyLanguage));
		}
		finally
		{
			TryDeleteDatabase(path);
		}
	}

	private static void TryDeleteDatabase(string path)
	{
		try
		{
			File.Delete(path);
			File.Delete($"{path}-wal");
			File.Delete($"{path}-shm");
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void 敏感APIKey自动加解密透明存取()
	{
		const string plainKey = "sk-test-secret-key-123456789";
		_config.Set("llm_api_key", new ConfigValue.Text(plainKey));

		// 读取时透明解密
		ConfigValue? value = _config.Get("llm_api_key");
		Assert.NotNull(value);
		Assert.Equal(plainKey, value.ToStorage());

		// 底层 SQLite 里必须是新格式密文, 且不含明文 (三平台一致)
		using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT value FROM config WHERE key = 'llm_api_key'";
		string rawInDb = (string)cmd.ExecuteScalar()!;
		Assert.StartsWith(SecretProtector.Prefix, rawInDb, StringComparison.Ordinal);
		Assert.DoesNotContain(plainKey, rawInDb, StringComparison.Ordinal);
	}

	[Fact]
	public void 换了主密钥的密文读不出明文而不是崩掉()
	{
		const string plainKey = "sk-rotate-me";
		_config.Set("llm_api_key", new ConfigValue.Text(plainKey));
		string cipher = _config.RawValue("llm_api_key");

		// 另一把密钥的 store 读同一条记录: 拿不到明文, 但也不能抛
		ConfigStore other = new(_database, new WrongKeyStore());
		string readBack = other.GetStringOr("llm_api_key", "");
		Assert.Equal("", readBack);
		Assert.NotEqual(plainKey, readBack);
		SecretIssue? issue = other.GetSecretIssue("llm_api_key");
		Assert.Equal(SecretIssueCategory.CorruptCiphertext, issue?.Category);
		Assert.StartsWith(SecretProtector.Prefix, cipher, StringComparison.Ordinal);
	}

	[Fact]
	public void nsec1读取后惰性迁移到nsec2()
	{
		string legacy = ProtectLegacyNsec1(Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray(), "legacy-secret");
		_database.Locked(connection =>
		{
			using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT INTO config (key, value) VALUES ('llm_api_key', $value)";
			command.Parameters.AddWithValue("$value", legacy);
			command.ExecuteNonQuery();
		});

		Assert.Equal("legacy-secret", _config.GetStringOr("llm_api_key", ""));
		Assert.StartsWith(SecretProtector.Prefix, _config.RawValue("llm_api_key"), StringComparison.Ordinal);
	}

	/// <summary>测试本地的 nsec1 造数: base64(nonce|cipher|tag), 无 AAD, 与已发布格式一致。</summary>
	private static string ProtectLegacyNsec1(byte[] key, string plainText)
	{
		const int nonceSize = 12;
		const int tagSize = 16;
		byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(nonceSize);
		byte[] plain = System.Text.Encoding.UTF8.GetBytes(plainText);
		byte[] cipher = new byte[plain.Length];
		byte[] tag = new byte[tagSize];
		using System.Security.Cryptography.AesGcm aes = new(key, tagSize);
		aes.Encrypt(nonce, plain, cipher, tag);
		byte[] payload = new byte[nonceSize + cipher.Length + tagSize];
		nonce.CopyTo(payload.AsSpan(0, nonceSize));
		cipher.CopyTo(payload.AsSpan(nonceSize, cipher.Length));
		tag.CopyTo(payload.AsSpan(nonceSize + cipher.Length, tagSize));
		return SecretProtector.LegacyNsec1Prefix + Convert.ToBase64String(payload);
	}

	[Fact]
	public void 损坏密文按未配置处理并记录分类()
	{
		_database.Locked(connection =>
		{
			using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT INTO config (key, value) VALUES ('llm_api_key', 'nsec2:not-valid')";
			command.ExecuteNonQuery();
		});

		Assert.Equal("", _config.GetStringOr("llm_api_key", ""));
		Assert.Equal(SecretIssueCategory.CorruptCiphertext, _config.GetSecretIssue("llm_api_key")?.Category);
	}

	[Fact]
	public void 密钥库不可用时拒绝写入且不落明文()
	{
		ConfigStore failing = new(_database, new ThrowingKeyStore());

		Assert.Throws<SecretKeyStoreException>(() => failing.Set("llm_api_key", new ConfigValue.Text("must-not-leak")));
		Assert.False(failing.Exists("llm_api_key"));
		Assert.DoesNotContain("must-not-leak", failing.RawValue("llm_api_key"), StringComparison.Ordinal);
	}

	[Fact]
	public void 旧布尔遥测迁移为三态同意()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-telemetry-migration-{Guid.NewGuid():N}.db");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			database.Locked(connection =>
			{
				using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
				command.CommandText = "INSERT INTO config (key, value) VALUES ('config_schema_version', '2'), ('telemetry_enabled', '1')";
				command.ExecuteNonQuery();
			});

			ConfigStore config = new(database);
			config.EnsureSchemaVersion();

			Assert.Equal(TelemetryConsent.Unset, config.GetTelemetryConsent());
			Assert.False(config.Exists(ConfigStore.KeyTelemetryEnabled));
			config.SetTelemetryConsent(TelemetryConsent.Granted);
			Assert.Equal(TelemetryConsent.Granted, config.GetTelemetryConsent());
		}
		finally
		{
			TryDeleteDatabase(path);
		}
	}

	[Fact]
	public void 旧布尔遥测false迁移为denied()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-telemetry-denied-{Guid.NewGuid():N}.db");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			database.Locked(connection =>
			{
				using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
				command.CommandText = "INSERT INTO config (key, value) VALUES ('config_schema_version', '2'), ('telemetry_enabled', '0')";
				command.ExecuteNonQuery();
			});

			ConfigStore config = new(database);
			config.EnsureSchemaVersion();

			Assert.Equal(TelemetryConsent.Denied, config.GetTelemetryConsent());
		}
		finally
		{
			TryDeleteDatabase(path);
		}
	}

	[Fact]
	public void 非敏感键不加密()
	{
		_config.Set("llm_model", new ConfigValue.Text("gpt-x"));
		Assert.Equal("gpt-x", _config.RawValue("llm_model"));
	}

	private sealed class WrongKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Repeat((byte)0xAB, SecretKeyStore.KeySize).ToArray();
		public byte[] LoadOrCreate() => _key;
		public bool IsFileFallback => true;
	}

	private sealed class ThrowingKeyStore : ISecretKeyStore
	{
		public byte[] LoadOrCreate() => throw new InvalidOperationException("keystore unavailable");
		public bool IsFileFallback => false;
	}
}
