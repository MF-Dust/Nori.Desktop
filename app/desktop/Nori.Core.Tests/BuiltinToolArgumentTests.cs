using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Emotion;
using Nori.Core.Embedding;
using Nori.Core.Logging;
using Nori.Core.Memory;
using Nori.Core.Network;
using Nori.Core.Proactive;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 工具参数里的数字。
///
/// 模型发数字参数时**经常带引号**：实测 qwen3.8-flash 调 setReminder 发的是
/// <c>{"content":"起来活动一下","delayMinutes":"20"}</c>。而
/// <c>JsonValue.TryGetValue&lt;double&gt;</c> 对 JSON 字符串一律返回 false，
/// 于是参数变成 null。
///
/// 这个坑有两种长相，后一种更难查：
/// - setReminder / deleteMemory 会抛「缺少参数」——参数在，报的却是不在；
/// - setEmotion / saveMemory 走的是 <c>?? 默认值</c>，**静默**用默认值顶上，
///   她照样回「记住了」，只是存进去的强度和重要度是错的。
///
/// 这一族钉住「带引号的数字要认、认不出来的要说清楚是什么」。
/// </summary>
public sealed class BuiltinToolArgumentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-args-" + Guid.NewGuid().ToString("N"));
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly ToolRegistry _tools = new();
	private readonly ProactiveScheduler _proactive;
	private readonly EmotionManager _emotion;

	public BuiltinToolArgumentTests()
	{
		AppStoragePaths paths = new(_root);
		Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
		_database = NoriDatabase.Open(paths.DatabasePath, paths);
		_config = new ConfigStore(_database, new Security.SecretKeyStore(paths));
		_emotion = new EmotionManager(_config);
		_proactive = new ProactiveScheduler(
			new ReminderStore(_database), _config, new FileLogger(paths.LogsDirectory), () => null);

		using HttpClient http = new();
		BuiltinTools.RegisterAll(_tools, new BuiltinToolDeps
		{
			Memory = new MemoryService(new MemoryStore(_database), new NoEmbedding(), _config, startBackgroundWorker: false),
			Emotion = _emotion,
			Proactive = _proactive,
			SystemInfo = new StubSystemInfo(),
			Fetcher = new WebPageFetcher(http),
			Http = http,
			Config = _config,
		});
	}

	private async Task<ToolResult> Call(string name, JsonNode arguments) =>
		await _tools.ExecuteAsync(name, arguments, new ToolContext
		{
			SessionId = "args",
			CancellationToken = CancellationToken.None,
			Approve = _ => Task.FromResult(true),
		});

	[Fact]
	public async Task 带引号的分钟数也能设上提醒()
	{
		ToolResult result = await Call("setReminder",
			new JsonObject {["content"] = "起来活动一下", ["delayMinutes"] = "20"});

		Assert.Null(result.Error);
		ReminderItem only = Assert.Single(_proactive.ListReminders());
		Assert.Equal("起来活动一下", only.Content);

		// 20 分钟后, 留一分钟余量给测试机上的耗时。
		double minutes = (DateTimeOffset.FromUnixTimeMilliseconds(only.TriggerAt) - DateTimeOffset.UtcNow).TotalMinutes;
		Assert.InRange(minutes, 19, 20.1);
	}

	[Fact]
	public async Task 不带引号的分钟数照旧能设上()
	{
		ToolResult result = await Call("setReminder",
			new JsonObject {["content"] = "喝水", ["delayMinutes"] = 5});

		Assert.Null(result.Error);
		Assert.Single(_proactive.ListReminders());
	}

	/// <summary>
	/// 实机形状：参数是从模型输出**解析**出来的，不是代码里构的。
	///
	/// 这两条来路的 JsonValue 底层不是一个东西（解析的是 JsonElement 支撑，
	/// 代码构的是 CLR int 支撑），<c>TryGetValue&lt;double&gt;</c> 的结果也不一样。
	/// 只用代码构的那种做测试，会漏掉另一半。
	/// </summary>
	[Theory]
	[InlineData("""{"content":"起来活动一下","delayMinutes":20}""")]
	[InlineData("""{"content":"起来活动一下","delayMinutes":"20"}""")]
	[InlineData("""{"content":"起来活动一下","delayMinutes":20.0}""")]
	public async Task 解析出来的分钟数三种写法都认(string raw)
	{
		ToolResult result = await Call("setReminder", JsonNode.Parse(raw)!);

		Assert.Null(result.Error);
		ReminderItem only = Assert.Single(_proactive.ListReminders());
		double minutes = (DateTimeOffset.FromUnixTimeMilliseconds(only.TriggerAt) - DateTimeOffset.UtcNow).TotalMinutes;
		Assert.InRange(minutes, 19, 20.1);
	}

	/// <summary>
	/// 「二十」这种不是数字, 仍然要拒绝 —— 这条修的是引号不是语义, 别顺手把
	/// 一个解析不了的值当成默认值放过去。
	/// </summary>
	[Fact]
	public async Task 不是数字的分钟数仍然拒绝()
	{
		ToolResult result = await Call("setReminder",
			new JsonObject {["content"] = "喝水", ["delayMinutes"] = "二十"});

		Assert.NotNull(result.Error);
		Assert.Empty(_proactive.ListReminders());
	}

	/// <summary>
	/// 报错要说清楚是「没给」还是「给了但不是数字」。原来两种都报「缺少参数」，
	/// 排查时会去找一个明明已经传了的参数。
	/// </summary>
	[Fact]
	public async Task 缺参数和参数不是数字报的不是同一句话()
	{
		ToolResult missing = await Call("setReminder", new JsonObject {["content"] = "喝水"});
		ToolResult wrong = await Call("setReminder",
			new JsonObject {["content"] = "喝水", ["delayMinutes"] = "二十"});

		Assert.NotNull(missing.Error);
		Assert.NotNull(wrong.Error);
		Assert.Contains("缺少参数", missing.Error);
		Assert.DoesNotContain("缺少参数", wrong.Error);
		// 报错里要带上她实际发了什么, 否则还得回去翻日志。
		Assert.Contains("二十", wrong.Error);
	}

	/// <summary>
	/// 这条是那两个**静默**走默认值的。带引号的 0.3 必须真的落成 0.3，
	/// 不能悄悄变回 0.8 —— 那种失败不报错，只会让她的情绪一直是同一个强度。
	/// </summary>
	[Fact]
	public async Task 带引号的情绪强度不会被默认值顶掉()
	{
		ToolResult result = await Call("setEmotion",
			new JsonObject {["emotion"] = EmotionTypes.Happy, ["intensity"] = "0.3"});

		Assert.Null(result.Error);
		Assert.Equal(0.3, _emotion.GetState().Intensity, 3);
	}

	[Fact]
	public async Task 没给强度时仍然用默认值()
	{
		ToolResult result = await Call("setEmotion", new JsonObject {["emotion"] = EmotionTypes.Happy});

		Assert.Null(result.Error);
		Assert.Equal(0.8, _emotion.GetState().Intensity, 3);
	}

	/// <summary>小数点用的是 JSON 的写法, 不跟系统区域走。</summary>
	[Fact]
	public async Task 小数按不变文化解析()
	{
		System.Globalization.CultureInfo before = Thread.CurrentThread.CurrentCulture;
		Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
		try
		{
			ToolResult result = await Call("setEmotion",
				new JsonObject {["emotion"] = EmotionTypes.Sad, ["intensity"] = "0.25"});

			Assert.Null(result.Error);
			Assert.Equal(0.25, _emotion.GetState().Intensity, 3);
		}
		finally
		{
			Thread.CurrentThread.CurrentCulture = before;
		}
	}

	public void Dispose()
	{
		_proactive.Dispose();
		_emotion.Dispose();
		_database.Dispose();
		try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清不掉不算失败 */ }
	}

	private sealed class StubSystemInfo : ISystemInfoProvider
	{
		public object GetInfo() => new {platform = "test"};
		public object? GetBatteryStatus() => null;
	}

	private sealed class NoEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string b, string k, string m, string i, int? d = null, CancellationToken t = default) =>
			Task.FromResult(Array.Empty<float>());

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string b, string k, string m, IReadOnlyList<string> i, int? d = null, CancellationToken t = default) =>
			Task.FromResult<IReadOnlyList<float[]>>([]);

		public void ClearCache() { }
	}
}
