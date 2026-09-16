using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// 登录态的落盘。
///
/// 跑在真的 SQLite 上而不是内存假件：这一族里最要紧的一条是「令牌落盘时确实被加密了」，
/// 而加密是 <see cref="ConfigStore"/> 按键名后缀决定的 —— 换成假件就把要测的那一层
/// 换掉了。
/// </summary>
public sealed class AccountSessionTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-test-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly AccountSession _session;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = [.. Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index)];
		public byte[] LoadOrCreate() => _key;
		public bool IsFileFallback => true;
	}

	public AccountSessionTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("0.1.0");
		_config.EnsureSchemaVersion();
		_session = new AccountSession(_config);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	private static CloudAccount Sample(string token = "s3ss10n-t0k3n-abcdef0123456789") => new()
	{
		Email = "nori@example.com",
		Name = "Nori",
		Token = token,
		ExpiresAt = "2027-01-01T00:00:00.000Z",
	};

	[Fact]
	public void 未登录时为空()
	{
		Assert.Null(_session.Current);
		Assert.False(_session.IsSignedIn);
	}

	[Fact]
	public void 登录后能原样读回()
	{
		_session.Save(Sample(), SignInMethod.Code);

		CloudAccount? current = _session.Current;
		Assert.NotNull(current);
		Assert.Equal("nori@example.com", current.Email);
		Assert.Equal("Nori", current.Name);
		Assert.Equal("s3ss10n-t0k3n-abcdef0123456789", current.Token);
		Assert.Equal("2027-01-01T00:00:00.000Z", current.ExpiresAt);
		Assert.True(_session.IsSignedIn);
	}

	/// <summary>
	/// 令牌在库里必须是密文。
	///
	/// 这条靠的是键名以 <c>_token</c> 结尾 —— <see cref="ConfigStore.IsSensitiveKey"/> 据此
	/// 决定加不加密。把键改成 <c>cloud_session</c> 之类，令牌会以明文落盘，而且不会有任何
	/// 报错、功能也完全正常。所以这里直接读原始存储做断言。
	/// </summary>
	[Fact]
	public void 令牌在库里是密文()
	{
		const string token = "s3ss10n-t0k3n-abcdef0123456789";
		_session.Save(Sample(token), SignInMethod.Code);

		string stored = _config.RawValue(AccountSession.TokenKey);
		Assert.NotEmpty(stored);
		Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
	}

	[Fact]
	public void 键名后缀必须让令牌落进敏感判定()
	{
		// 上一条测的是结果，这一条钉住原因 —— 结果那条在别人改了加密实现之后可能
		// 以别的方式通过，而这条只在键名被改时失败。
		Assert.True(ConfigStore.IsSensitiveKey(AccountSession.TokenKey));
	}

	[Fact]
	public void 退出后读不到账户()
	{
		_session.Save(Sample(), SignInMethod.Code);
		_session.Clear();

		Assert.Null(_session.Current);
		Assert.False(_session.IsSignedIn);
	}

	/// <summary>
	/// 换账户登录要把本机记的云端版本号清零。
	///
	/// 版本号是对**某一个账户的存档**说的。留着上一个账户的号，下一次备份会带着它去跟
	/// 新账户的存档比：新账户还没有存档（版本 0），服务端回 409，客户端把它显示成
	/// 「云端有更新的存档」—— 那份存档并不存在，而用户只能被迫选覆盖。
	/// </summary>
	[Fact]
	public void 换账户登录时清掉本机版本号()
	{
		_session.Save(Sample(), SignInMethod.Code);
		_config.Set(CloudSyncService.RevisionKey, new ConfigValue.Text("r7"));

		_session.Save(Sample() with {Email = "other@example.com"}, SignInMethod.Code);

		Assert.Equal(0, CloudSyncService.KnownRevisionOf(_config));
	}

	/// <summary>同一个账户重新登录不清版本号：那个号仍然成立，清掉只会多一次覆盖确认。</summary>
	[Fact]
	public void 同一账户重新登录保留版本号()
	{
		_session.Save(Sample(), SignInMethod.Code);
		_config.Set(CloudSyncService.RevisionKey, new ConfigValue.Text("r7"));

		_session.Clear();
		_session.Save(Sample(token: "another-token-0123456789abcdef"), SignInMethod.Code);

		Assert.Equal(7, CloudSyncService.KnownRevisionOf(_config));
	}

	/// <summary>退出登录不该把「上次用哪种方式登的」也忘掉 —— 那是本机偏好，不是登录态。</summary>
	[Fact]
	public void 退出后仍记得上次的登录方式()
	{
		_session.Save(Sample(), SignInMethod.Password);
		Assert.Equal(SignInMethod.Password, _session.LastMethod);

		_session.Clear();
		Assert.Equal(SignInMethod.Password, _session.LastMethod);
	}

	/// <summary>没设过时默认验证码方式：线上账户多数没有密码，默认密码框会让他们先失败一次。</summary>
	[Fact]
	public void 默认是验证码方式()
	{
		Assert.Equal(SignInMethod.Code, _session.LastMethod);
	}

	/// <summary>
	/// 只剩半份记录时视为未登录。
	///
	/// 只有邮箱没有令牌发不出任何请求，界面却会显示「已登录」——
	/// 那种状态比干脆没登录更难排查。
	/// </summary>
	[Fact]
	public void 半份记录视为未登录()
	{
		_session.Save(Sample(), SignInMethod.Code);
		_config.Set(AccountSession.TokenKey, new ConfigValue.Text(""));

		Assert.Null(_session.Current);
	}

	/// <summary>
	/// 配置读取会**重新推断类型**（见 ConfigStore 的说明）。
	///
	/// 全数字的令牌因此可能被读成整数再格式化回来。这条钉住那一趟往返不丢字符 ——
	/// 真出问题时症状是登录之后每个请求都 401，而本机显示一切正常。
	/// </summary>
	[Theory]
	[InlineData("1234567890")]
	[InlineData("0001")]
	[InlineData("98765432109876543210987654321098")]
	public void 全数字令牌也能原样读回(string token)
	{
		_session.Save(Sample(token), SignInMethod.Code);
		Assert.Equal(token, _session.Current?.Token);
	}
}
