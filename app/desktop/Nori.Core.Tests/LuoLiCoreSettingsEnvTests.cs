using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// LuoLiCore 配置的环境变量兜底。
///
/// 原生 UI 迁移期间还没有填这几项的地方，密钥本来也不该写进文件。规则只有一条：
/// **配置优先、环境变量兜底** —— 反过来的话，将来设置页改了值却不生效，那种错位很难查。
/// </summary>
public sealed class LuoLiCoreSettingsEnvTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-luoli-env-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public LuoLiCoreSettingsEnvTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("test");
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	/// <summary>不去改进程环境：那会串到并行跑的其他测试里，表现成偶发失败。</summary>
	private LuoLiCoreSettingsStore Store(params (string Name, string Value)[] environment)
	{
		Dictionary<string, string> table = environment.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
		return new LuoLiCoreSettingsStore(_config, name => table.GetValueOrDefault(name));
	}

	[Fact]
	public void 环境变量给齐地址与密钥就自动启用()
	{
		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read();

		// 迁移期没有界面可点，还要求单独设一个开关只会让人以为没生效。
		Assert.True(settings.IsActive);
		Assert.Equal("http://127.0.0.1:3000", settings.BaseUrl);
		Assert.Equal("sk-env", settings.ApiKey);
	}

	[Fact]
	public void 只给一半不算启用()
	{
		Assert.False(Store((LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000")).Read().IsActive);
		Assert.False(Store((LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read().IsActive);
	}

	[Fact]
	public void 环境变量里显式关掉就不启用()
	{
		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvEnabled, "0"),
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read();

		Assert.False(settings.IsActive);
		// 关掉的只是开关，地址密钥仍然读得到 —— 打开就能用，不用重填。
		Assert.True(settings.IsConfigured);
	}

	[Fact]
	public void 配置优先于环境变量()
	{
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://127.0.0.1:4000"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("sk-config"));

		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read();

		Assert.Equal("http://127.0.0.1:4000", settings.BaseUrl);
		Assert.Equal("sk-config", settings.ApiKey);
	}

	/// <summary>配置里显式关掉，环境变量不该把它顶回开。</summary>
	[Fact]
	public void 配置里关掉时环境变量顶不开()
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(false));

		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvEnabled, "1"),
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read();

		Assert.False(settings.IsActive);
	}

	[Fact]
	public void 会话id也能从环境变量来()
	{
		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env"),
			(LuoLiCoreSettingsStore.EnvSessionId, "sess_env"),
			(LuoLiCoreSettingsStore.EnvModelAlias, "fast")).Read();

		Assert.Equal("sess_env", settings.SessionId);
		Assert.Equal("fast", settings.ToOptions().ModelAlias);
	}

	/// <summary>写回会话 id 之后就归配置管，换一台机器的环境变量不该再顶回去。</summary>
	[Fact]
	public void 写回的会话id盖过环境变量()
	{
		LuoLiCoreSettingsStore store = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env"),
			(LuoLiCoreSettingsStore.EnvSessionId, "sess_env"));

		store.SaveSessionId("sess_saved");

		Assert.Equal("sess_saved", store.Read().SessionId);
	}

	[Fact]
	public void 地址末尾的斜杠被去掉()
	{
		LuoLiCoreSettings settings = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000/"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env")).Read();

		Assert.Equal("http://127.0.0.1:3000", settings.BaseUrl);
	}

	// ---- 会话 id 绑在建它的那台服务端与那把密钥上（codex review，P2）----

	/// <summary>
	/// 换了地址之后旧会话 id 属于另一台服务端，继续拿它发消息只会一直 404 —— 而那个失败看
	/// 起来像「服务端坏了」，不像「你改了配置」。
	/// </summary>
	[Fact]
	public void 换了地址之后旧会话作废()
	{
		LuoLiCoreSettingsStore first = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env"));
		first.SaveSessionId("sess_old");
		Assert.Equal("sess_old", first.Read().SessionId);

		LuoLiCoreSettingsStore moved = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:4000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env"));

		Assert.Equal("", moved.Read().SessionId);
	}

	[Fact]
	public void 换了密钥之后旧会话同样作废()
	{
		LuoLiCoreSettingsStore first = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-old"));
		first.SaveSessionId("sess_old");

		LuoLiCoreSettingsStore rekeyed = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-new"));

		Assert.Equal("", rekeyed.Read().SessionId);
	}

	[Fact]
	public void 地址与密钥都没变时会话照旧()
	{
		(string, string)[] env =
		[
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-env"),
		];
		Store(env).SaveSessionId("sess_old");

		Assert.Equal("sess_old", Store(env).Read().SessionId);
	}

	/// <summary>指纹这一列不加密，把密钥原样写进去等于绕开 `_api_key` 那套加密存储。</summary>
	[Fact]
	public void 指纹里不含密钥原文()
	{
		LuoLiCoreSettingsStore store = Store(
			(LuoLiCoreSettingsStore.EnvBaseUrl, "http://127.0.0.1:3000"),
			(LuoLiCoreSettingsStore.EnvApiKey, "sk-super-secret"));
		store.SaveSessionId("sess_old");

		string stored = _config.GetStringOr(LuoLiCoreSettingsStore.KeySessionOwner, "");

		Assert.DoesNotContain("sk-super-secret", stored, StringComparison.Ordinal);
		Assert.DoesNotContain("127.0.0.1", stored, StringComparison.Ordinal);
		Assert.Equal(64, stored.Length);
	}
}
