using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// 一轮对话里的工具轮数上限。
///
/// 原先写死 5。一个「先搜、再读、再改、再验」的任务在第五轮刚好卡在验证之前 —— 她做了一半
/// 就停下，对用户表现成「没做完也没说为什么」。放宽不增加简单对话的开销：模型不调工具时
/// 循环自己就结束了，上限只决定它最多能走多远。
/// </summary>
public sealed class AgentToolIterationsTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-iter-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public AgentToolIterationsTests()
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

	/// <summary>
	/// 通过反射读私有属性。
	///
	/// 这条判据本身是「配置能不能改到循环上限」，而把它做成公开 API 只是为了方便测试 ——
	/// 那会让一个内部决定变成对外承诺。
	/// </summary>
	private int Budget(AgentEngine engine) =>
		(int)typeof(AgentEngine)
			.GetProperty("ToolIterations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(engine)!;

	private AgentEngine Build(int? explicitLimit = null)
	{
		ChatService chat = new(new HttpClient(), _database, _config);
		return explicitLimit is null
			? new AgentEngine(
				new HttpClient(), _config, chat,
				tools: null!, skills: null!, emotion: null!, memory: null!,
				pet: null, motionNames: () => [], expressionNames: () => [])
			: new AgentEngine(
				new HttpClient(), _config, chat,
				tools: null!, skills: null!, emotion: null!, memory: null!,
				pet: null, motionNames: () => [], expressionNames: () => [],
				maxToolIterations: explicitLimit.Value);
	}

	[Fact]
	public void 没配过时用默认值()
	{
		Assert.Equal(AgentEngine.DefaultToolIterations, Budget(Build()));
	}

	[Fact]
	public void 配置能改到循环上限()
	{
		_config.Set(ConfigStore.KeyAgentMaxToolIterations, new ConfigValue.Text("20"));

		Assert.Equal(20, Budget(Build()));
	}

	/// <summary>上界不是为了省钱，是为了让一个跑飞的循环有个尽头。</summary>
	[Theory]
	[InlineData("0", AgentEngine.MinToolIterations)]
	[InlineData("-5", AgentEngine.MinToolIterations)]
	[InlineData("999", AgentEngine.MaxToolIterationsLimit)]
	public void 超出范围的配置被夹回边界(string configured, int expected)
	{
		_config.Set(ConfigStore.KeyAgentMaxToolIterations, new ConfigValue.Text(configured));

		Assert.Equal(expected, Budget(Build()));
	}

	/// <summary>一个打错的配置值不该让整条对话路径失败。</summary>
	[Fact]
	public void 配的不是数就退回默认()
	{
		_config.Set(ConfigStore.KeyAgentMaxToolIterations, new ConfigValue.Text("很多次"));

		Assert.Equal(AgentEngine.DefaultToolIterations, Budget(Build()));
	}

	/// <summary>构造时显式给了值就以它为准，不受用户配置干扰。</summary>
	[Fact]
	public void 显式传入的上限盖过配置()
	{
		_config.Set(ConfigStore.KeyAgentMaxToolIterations, new ConfigValue.Text("20"));

		Assert.Equal(3, Budget(Build(explicitLimit: 3)));
	}

	[Fact]
	public void 默认值比原先的五轮宽()
	{
		// 这一条钉住的是这次改动的意图本身：五轮不够。
		Assert.True(AgentEngine.DefaultToolIterations > 5);
	}
}
