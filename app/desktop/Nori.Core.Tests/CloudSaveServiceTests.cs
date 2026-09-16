using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Memory;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

/// <summary>
/// 云存档的打包与恢复。
///
/// 这一族覆盖三类在实现上容易做错、且做错了不会报错的要求：
/// 密钥不得离开本机、恢复不得删除本机数据、同一份存档恢复两次结果相同。
/// </summary>
public sealed class CloudSaveServiceTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-cloud-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryStore _memoryStore;
	private readonly MemoryTransferService _transfer;
	private readonly ReminderStore _reminders;
	private readonly CloudSaveService _service;
	private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));

	public CloudSaveServiceTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database);
		_memoryStore = new MemoryStore(_database);
		_transfer = new MemoryTransferService(_memoryStore, timeProvider: _clock);
		_reminders = new ReminderStore(_database);
		_service = new CloudSaveService(_config, _transfer, _reminders, _clock);
	}

	public void Dispose()
	{
		_transfer.Dispose();
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	// ── 打包 ────────────────────────────────────────────────────────────────

	[Fact]
	public void 打包带上格式标识与时间()
	{
		CloudSaveBuild build = _service.Build("0.2.0");

		Assert.Equal(CloudSaveDocument.FormatName, build.Document.Format);
		Assert.Equal("0.2.0", build.Document.AppVersion);
		Assert.StartsWith("2026-09-14", build.Document.SavedAt);
		Assert.True(build.Bytes > 0);
	}

	[Fact]
	public void 只打包范围内的配置()
	{
		_config.Set("language", new ConfigValue.Text("zh-CN"));
		_config.Set("l2d_scale", new ConfigValue.Text("1.2"));
		_config.Set("l2d_scale_nori", new ConfigValue.Text("0.8"));
		// 范围之外的三类：本机路径、本机安全决定、本机安装事实。
		_config.Set("workspace_root", new ConfigValue.Text("D:/somewhere"));
		_config.Set("permission_gear", new ConfigValue.Text("open"));
		_config.Set("installed_at", new ConfigValue.Text("2026-01-01"));

		CloudSaveBuild build = _service.Build();

		Assert.Equal("zh-CN", build.Document.Config["language"]);
		Assert.Equal("1.2", build.Document.Config["l2d_scale"]);
		Assert.Equal("0.8", build.Document.Config["l2d_scale_nori"]);
		Assert.DoesNotContain("workspace_root", build.Document.Config.Keys);
		Assert.DoesNotContain("permission_gear", build.Document.Config.Keys);
		Assert.DoesNotContain("installed_at", build.Document.Config.Keys);
	}

	/// <summary>
	/// **密钥不得出现在存档里。**
	///
	/// 白名单里本来就没有密钥（CloudSaveScopeTests 有一条专门扫这件事），这里验的是
	/// 序列化之后的字节里也搜不到 —— 这是数据真正离开本机的那一步。
	/// </summary>
	[Fact]
	public void 密钥不进存档()
	{
		const string Secret = "sk-should-never-leave-this-machine";
		_config.Set("llm_api_key", new ConfigValue.Text(Secret));
		_config.Set("tts_api_key", new ConfigValue.Text(Secret));
		_config.Set("language", new ConfigValue.Text("zh-CN"));

		CloudSaveBuild build = _service.Build();
		string wire = _service.Serialize(build.Document);

		Assert.DoesNotContain(Secret, wire, StringComparison.Ordinal);
		Assert.DoesNotContain("llm_api_key", wire, StringComparison.Ordinal);
		Assert.DoesNotContain("tts_api_key", wire, StringComparison.Ordinal);
		Assert.Contains("zh-CN", wire, StringComparison.Ordinal);
	}

	[Fact]
	public void 打包记忆与提醒()
	{
		_memoryStore.AddAggregate("factual", "她喜欢在晚上写代码", kind: MemoryKind.Factual);
		_reminders.Add("十点提醒喝水", 1_800_000_000_000);

		CloudSaveBuild build = _service.Build();

		Assert.Equal(1, build.MemoryCount);
		Assert.Equal(1, build.ReminderCount);
		Assert.Equal("十点提醒喝水", build.Document.Reminders[0].Content);
	}

	/// <summary>终态的提醒在本机已经结束，搬到另一台机器上只是历史噪声。</summary>
	[Fact]
	public void 终态的提醒不打包()
	{
		ReminderItem done = _reminders.Add("已经完成的", 1_800_000_000_000);
		ReminderItem cancelled = _reminders.Add("已经取消的", 1_800_000_000_000);
		_reminders.Add("还没到的", 1_800_000_000_000);
		_reminders.Complete(done.Id);
		_reminders.Cancel(cancelled.Id);

		CloudSaveBuild build = _service.Build();

		Assert.Equal(1, build.ReminderCount);
		Assert.Equal("还没到的", build.Document.Reminders[0].Content);
	}

	/// <summary>向量由本机的嵌入模型算出，换一台机器上的模型或维度不同，搬过去用不了。</summary>
	[Fact]
	public void 存档里不带向量()
	{
		_memoryStore.AddAggregate("factual", "带向量的记忆", kind: MemoryKind.Factual);
		string wire = _service.Serialize(_service.Build().Document);

		Assert.DoesNotContain("embedding", wire, StringComparison.OrdinalIgnoreCase);
	}

	// ── 解析 ────────────────────────────────────────────────────────────────

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("{ 这不是 json")]
	[InlineData("{\"format\":\"别的格式\"}")]
	[InlineData("{\"memories\":[]}")]
	public void 解析拒收形状不对的内容(string? content)
	{
		Assert.Null(CloudSaveService.Parse(content));
	}

	[Fact]
	public void 序列化之后解析得回来()
	{
		_config.Set("language", new ConfigValue.Text("en-US"));
		string wire = _service.Serialize(_service.Build().Document);

		CloudSaveDocument? parsed = CloudSaveService.Parse(wire);

		Assert.NotNull(parsed);
		Assert.Equal("en-US", parsed!.Config["language"]);
	}

	// ── 恢复 ────────────────────────────────────────────────────────────────

	[Fact]
	public void 恢复写回配置()
	{
		_config.Set("language", new ConfigValue.Text("zh-CN"));
		CloudSaveDocument saved = _service.Build().Document;

		_config.Set("language", new ConfigValue.Text("en-US"));
		CloudRestoreResult result = _service.Restore(saved);

		Assert.True(result.Succeeded);
		Assert.Equal(1, result.ConfigApplied);
		Assert.Equal("zh-CN", _config.GetStringOr("language", ""));
	}

	/// <summary>
	/// **存档是从网络上来的，里面的键不能信。**
	///
	/// 服务端被攻破、或者中间被改过的存档不该能借恢复这条路写到本机的路径、
	/// 授权档位或密钥上。
	/// </summary>
	[Fact]
	public void 恢复忽略范围外的键()
	{
		CloudSaveDocument tampered = new()
		{
			Format = CloudSaveDocument.FormatName,
			Config = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["language"] = "zh-CN",
				["workspace_root"] = "C:/attacker",
				["permission_gear"] = "open",
				["automation_allow_keyboard"] = "true",
				["llm_api_key"] = "sk-injected",
			},
		};

		CloudRestoreResult result = _service.Restore(tampered);

		Assert.True(result.Succeeded);
		Assert.Equal(1, result.ConfigApplied);
		Assert.Equal("zh-CN", _config.GetStringOr("language", ""));
		Assert.Null(_config.Get("workspace_root"));
		Assert.Null(_config.Get("permission_gear"));
		Assert.Null(_config.Get("automation_allow_keyboard"));
		Assert.Null(_config.Get("llm_api_key"));
		Assert.Equal(4, result.Skipped.Count);
	}

	[Fact]
	public void 恢复格式不对的存档不做任何改动()
	{
		_config.Set("language", new ConfigValue.Text("zh-CN"));

		CloudRestoreResult result = _service.Restore(new CloudSaveDocument { Format = "别的格式" });

		Assert.False(result.Succeeded);
		Assert.NotEqual("", result.Error);
		Assert.Equal("zh-CN", _config.GetStringOr("language", ""));
	}

	[Fact]
	public void 恢复合并记忆与提醒()
	{
		_memoryStore.AddAggregate("factual", "存档里的记忆", kind: MemoryKind.Factual);
		ReminderItem reminder = _reminders.Add("存档里的提醒", 1_800_000_000_000);
		CloudSaveDocument saved = _service.Build().Document;

		// 换一台「机器」：同一套 schema，空的数据。
		using Fixture other = new();
		CloudRestoreResult result = other.Service.Restore(saved);

		Assert.True(result.Succeeded);
		Assert.Equal(1, result.MemoriesAdded);
		Assert.Equal(1, result.RemindersAdded);
		Assert.Equal("存档里的提醒", other.Reminders.Get(reminder.Id)?.Content);
	}

	/// <summary>
	/// **同一份存档恢复两次，结果与恢复一次相同。**
	///
	/// 不幂等的话每点一次「恢复」记忆和提醒就翻一倍，而用户在网络不稳时会重试。
	/// 记忆靠 MemoryTransferService 的去重，提醒靠原样保留的 id。
	/// </summary>
	[Fact]
	public void 恢复两次与恢复一次结果相同()
	{
		_memoryStore.AddAggregate("factual", "只该出现一次", kind: MemoryKind.Factual);
		_reminders.Add("也只该出现一次", 1_800_000_000_000);
		CloudSaveDocument saved = _service.Build().Document;

		using Fixture other = new();
		other.Service.Restore(saved);
		CloudRestoreResult again = other.Service.Restore(saved);

		Assert.True(again.Succeeded);
		Assert.Equal(0, again.MemoriesAdded);
		Assert.Equal(0, again.RemindersAdded);
		Assert.Single(other.MemoryStore.GetAll(100));
		Assert.Single(other.Reminders.List());
	}

	/// <summary>
	/// **恢复不删除本机已有的东西。**
	///
	/// 存档是某一时刻的快照。拿它去删本机数据意味着「在 A 机删一条」变成
	/// 「在 B 机也删」，而用户没有表达这个意图，且不可撤销。
	/// </summary>
	[Fact]
	public void 恢复不删除本机已有的记忆与提醒()
	{
		CloudSaveDocument empty = _service.Build().Document;

		using Fixture other = new();
		other.MemoryStore.AddAggregate("factual", "本机自己的记忆", kind: MemoryKind.Factual);
		other.Reminders.Add("本机自己的提醒", 1_800_000_000_000);

		other.Service.Restore(empty);

		Assert.Single(other.MemoryStore.GetAll(100));
		Assert.Single(other.Reminders.List());
	}

	/// <summary>id 相同说明是同一条提醒，以存档为准更新内容，不新增第二条。</summary>
	[Fact]
	public void 同一条提醒按id更新而不是再加一条()
	{
		ReminderItem reminder = _reminders.Add("原来的内容", 1_800_000_000_000);
		CloudSaveDocument saved = _service.Build().Document;

		using Fixture other = new();
		other.Reminders.AddExact(reminder.Id, "另一台机器上的旧内容", 1_700_000_000_000,
			false, "UTC", null, null);

		CloudRestoreResult result = other.Service.Restore(saved);

		Assert.Equal(0, result.RemindersAdded);
		Assert.Equal(1, result.RemindersUpdated);
		Assert.Single(other.Reminders.List());
		Assert.Equal("原来的内容", other.Reminders.Get(reminder.Id)?.Content);
	}

	[Fact]
	public void 缺少id或内容的提醒被忽略()
	{
		CloudSaveDocument broken = new()
		{
			Format = CloudSaveDocument.FormatName,
			Reminders =
			[
				new CloudReminder { Id = null, Content = "没有 id" },
				new CloudReminder { Id = "有 id", Content = "   " },
			],
		};

		CloudRestoreResult result = _service.Restore(broken);

		Assert.True(result.Succeeded);
		Assert.Equal(0, result.RemindersAdded);
		Assert.Equal(2, result.Skipped.Count);
		Assert.Empty(_reminders.List());
	}

	/// <summary>固定时钟。与本仓库其余测试一致，各自带一份私有实现。</summary>
	private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan duration) => _now = _now.Add(duration);
	}

	/// <summary>另一套独立的本地存储，用来表示「另一台机器」。</summary>
	private sealed class Fixture : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-cloud-other-{Guid.NewGuid():N}.db");
		private readonly NoriDatabase _database;
		private readonly MemoryTransferService _transfer;

		internal Fixture()
		{
			_database = NoriDatabase.Open(_path);
			Config = new ConfigStore(_database);
			MemoryStore = new MemoryStore(_database);
			_transfer = new MemoryTransferService(MemoryStore);
			Reminders = new ReminderStore(_database);
			Service = new CloudSaveService(Config, _transfer, Reminders);
		}

		internal ConfigStore Config { get; }
		internal MemoryStore MemoryStore { get; }
		internal ReminderStore Reminders { get; }
		internal CloudSaveService Service { get; }

		public void Dispose()
		{
			_transfer.Dispose();
			_database.Dispose();
			try { File.Delete(_path); } catch (IOException) { /* 同上 */ }
		}
	}
}
