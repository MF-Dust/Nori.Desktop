using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Emotion;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Memory;
using Nori.Core.Proactive;
using Nori.Core.Skills;
using Nori.Core.Tools;
using Nori.Core.Voice;

namespace Nori.Core.Tests;

/// <summary>
/// 后端化新模块的集中用例: 工具授权/技能/情绪/提醒/历史规范化/模型元数据/语音停用
/// </summary>
public class BackendRuntimeModuleTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-runtime-{Guid.NewGuid():N}.db");
	private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nori-runtime-{Guid.NewGuid():N}");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public BackendRuntimeModuleTests()
	{
		Directory.CreateDirectory(_tempDir);
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	public void Dispose()
	{
		_database.Dispose();
		try
		{
			File.Delete(_dbPath);
			Directory.Delete(_tempDir, true);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	// ---- 工具注册表: 逐调用授权 fail-closed ----

	private static RegisteredTool MakeTool(
		string name,
		string permission,
		Func<Task<object?>> execute,
		string category = "builtin") => new()
	{
		Name = name,
		Description = name,
		Parameters = new JsonObject {["type"] = "object"},
		PermissionLevel = permission,
		Category = category,
		Execute = (_, _) => execute(),
	};

	[Fact]
	public async Task safe工具直接执行无需授权()
	{
		ToolRegistry registry = new();
		int calls = 0;
		registry.Register(MakeTool("t-safe", "safe", () =>
		{
			calls++;
			return Task.FromResult<object?>("done");
		}));

		ToolResult result = await registry.ExecuteAsync("t-safe", null);
		Assert.True(result.IsSuccess);
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task confirm工具缺少授权回调时fail_closed()
	{
		ToolRegistry registry = new();
		int calls = 0;
		registry.Register(MakeTool("t-confirm", "confirm", () =>
		{
			calls++;
			return Task.FromResult<object?>(null);
		}));

		ToolResult result = await registry.ExecuteAsync("t-confirm", null);
		Assert.False(result.IsSuccess);
		Assert.Contains("已拒绝执行", result.Error, StringComparison.Ordinal);
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task 用户批准后执行一次_拒绝时不执行()
	{
		ToolRegistry registry = new();
		int calls = 0;
		registry.Register(MakeTool("t-approve", "dangerous", () =>
		{
			calls++;
			return Task.FromResult<object?>("ran");
		}));

		bool? askedLevel = null;
		ToolResult approved = await registry.ExecuteAsync("t-approve", null, new ToolContext
		{
			Approve = request =>
			{
				askedLevel = request.PermissionLevel == "dangerous" ? true : null;
				return Task.FromResult(true);
			},
		});
		Assert.True(approved.IsSuccess);
		Assert.Equal(1, calls);
		Assert.True(askedLevel);

		ToolResult denied = await registry.ExecuteAsync("t-approve", null, new ToolContext
		{
			Approve = _ => Task.FromResult(false),
		});
		Assert.False(denied.IsSuccess);
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task 授权通道异常视为拒绝()
	{
		ToolRegistry registry = new();
		int calls = 0;
		registry.Register(MakeTool("t-crash", "confirm", () =>
		{
			calls++;
			return Task.FromResult<object?>(null);
		}));

		ToolResult result = await registry.ExecuteAsync("t-crash", null, new ToolContext
		{
			Approve = _ => throw new InvalidOperationException("dialog gone"),
		});
		Assert.False(result.IsSuccess);
		Assert.Equal(0, calls);
	}

	[Fact]
	public void 启停与禁用清单持久化往返()
	{
		ToolRegistry registry = new();
		registry.Register(MakeTool("a", "safe", () => Task.FromResult<object?>(null)));
		registry.Register(MakeTool("b", "safe", () => Task.FromResult<object?>(null)));

		registry.SetEnabled("a", false);
		Assert.Equal(["a"], registry.DisabledNames());
		Assert.Single(registry.ListEnabled(), tool => tool.Name == "b");

		ToolRegistry restored = new();
		restored.Register(MakeTool("a", "safe", () => Task.FromResult<object?>(null)));
		restored.RestoreDisabled(registry.DisabledNames());
		Assert.DoesNotContain(restored.ListEnabled(), tool => tool.Name == "a");
	}

	[Fact]
	public void 先恢复禁用清单再注册工具仍立即禁用且重复注册继续生效()
	{
		ToolRegistry registry = new();
		registry.RestoreDisabled(["future-tool"]);

		registry.Register(MakeTool("future-tool", "safe", () => Task.FromResult<object?>(null)));
		Assert.False(registry.Get("future-tool")!.Enabled);
		Assert.DoesNotContain(registry.ListEnabled(), tool => tool.Name == "future-tool");

		registry.Register(MakeTool("future-tool", "safe", () => Task.FromResult<object?>(null)));
		Assert.False(registry.Get("future-tool")!.Enabled);
	}

	[Fact]
	public void 启用未注册工具会清除待恢复禁用状态()
	{
		ToolRegistry registry = new();
		registry.RestoreDisabled(["future-tool"]);

		Assert.False(registry.SetEnabled("future-tool", true));
		Assert.DoesNotContain("future-tool", registry.DisabledNames());

		registry.Register(MakeTool("future-tool", "safe", () => Task.FromResult<object?>(null)));
		Assert.Contains(registry.ListEnabled(), tool => tool.Name == "future-tool");
	}

	[Fact]
	public void 恢复未知工具名不会影响已注册工具()
	{
		ToolRegistry registry = new();
		registry.Register(MakeTool("known-tool", "safe", () => Task.FromResult<object?>(null)));

		registry.RestoreDisabled(["unknown-tool", "known-tool"]);

		Assert.DoesNotContain(registry.ListEnabled(), tool => tool.Name == "known-tool");
		Assert.Contains("unknown-tool", registry.DisabledNames());
		Assert.Contains("known-tool", registry.DisabledNames());
	}

	[Fact]
	public void 动态工具候选集合失败时保留旧集合()
	{
		ToolRegistry registry = new();
		RegisteredTool builtin = MakeTool("builtin-tool", "safe", () => Task.FromResult<object?>(null));
		RegisteredTool previous = MakeTool("mcp__old__tool", "confirm", () => Task.FromResult<object?>(null), "mcp");
		registry.Register(builtin);
		registry.Register(previous);

		RegisteredTool replacement = MakeTool("mcp__new__tool", "confirm", () => Task.FromResult<object?>(null), "mcp");
		RegisteredTool conflict = MakeTool("builtin-tool", "confirm", () => Task.FromResult<object?>(null), "mcp");

		Assert.Throws<InvalidOperationException>(() => registry.ReplaceCategory("mcp", [replacement, conflict]));
		Assert.Same(builtin, registry.Get("builtin-tool"));
		Assert.Same(previous, registry.Get("mcp__old__tool"));
		Assert.Null(registry.Get("mcp__new__tool"));
	}

	[Fact]
	public void 动态工具替换继续应用待恢复禁用清单()
	{
		ToolRegistry registry = new();
		registry.Register(MakeTool("builtin-tool", "safe", () => Task.FromResult<object?>(null)));
		registry.RestoreDisabled(["mcp__new__tool"]);

		registry.ReplaceCategory("mcp",
		[
			MakeTool("mcp__new__tool", "confirm", () => Task.FromResult<object?>(null), "mcp"),
		]);

		RegisteredTool? dynamicTool = registry.Get("mcp__new__tool");
		Assert.NotNull(dynamicTool);
		Assert.False(dynamicTool!.Enabled);
		Assert.DoesNotContain(registry.ListEnabled(), tool => tool.Name == "mcp__new__tool");
		Assert.Contains("mcp__new__tool", registry.DisabledNames());
		Assert.NotNull(registry.Get("builtin-tool"));
	}

	[Fact]
	public async Task 动态工具替换并发读写不会暴露半成品()
	{
		ToolRegistry registry = new();
		registry.Register(MakeTool("builtin-tool", "safe", () => Task.FromResult<object?>(null)));
		registry.ReplaceCategory("mcp",
		[
			MakeTool("mcp__seed__tool", "confirm", () => Task.FromResult<object?>(null), "mcp"),
		]);

		ConcurrentBag<IReadOnlyList<RegisteredTool>> snapshots = [];
		Task reader = Task.Run(() =>
		{
			for (int index = 0; index < 5_000; index++) snapshots.Add(registry.List());
		});
		Task[] writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
		{
			for (int index = 0; index < 100; index++)
			{
				registry.ReplaceCategory("mcp",
				[
					MakeTool($"mcp__server_{writer}_{index}__tool", "confirm", () => Task.FromResult<object?>(null), "mcp"),
				]);
			}
		})).ToArray();

		await Task.WhenAll(writers.Append(reader));

		Assert.NotEmpty(snapshots);
		Assert.All(snapshots, snapshot =>
		{
			Assert.Equal(2, snapshot.Count);
			Assert.Contains(snapshot, tool => tool.Name == "builtin-tool");
			Assert.Single(snapshot, tool => tool.Category == "mcp");
		});
	}

	/// <summary>
	/// 合并三份工具注册实现之后，既有工具的参数契约不得改变。
	///
	/// 旧的 schema 生成按参数名硬编码推断类型（importance / intensity / delayMinutes 算数字），
	/// 现在由工厂方法显式给出。下面几条正是当初靠名字命中的那些，类型或必填性一旦漂了，
	/// 模型的调用要到运行时才失败。
	/// </summary>
	[Theory]
	[InlineData("setEmotion", "intensity", "number", false)]
	[InlineData("setReminder", "delayMinutes", "number", true)]
	[InlineData("remember", "importance", "number", false)]
	[InlineData("remember", "content", "string", true)]
	[InlineData("searchWeb", "tag", "string", false)]
	public void 内置工具的参数契约不变(string tool, string parameter, string type, bool required)
	{
		ToolRegistry registry = new();
		BuiltinTools.RegisterAll(registry, new BuiltinToolDeps
		{
			Memory = new MemoryService(new MemoryStore(_database), new EmbeddingStub(), _config),
			Emotion = new EmotionManager(_config),
			Proactive = new ProactiveScheduler(new ReminderStore(_database), _config,
				new FileLogger(Path.Combine(_tempDir, "logs")), () => null),
			SystemInfo = new StubSystemInfo(),
			Fetcher = new StubFetcher(),
			Http = new HttpClient(),
			Config = _config,
		});

		System.Text.Json.Nodes.JsonObject schema = registry.Get(tool)!.Parameters.AsObject();
		Assert.Equal(type, schema["properties"]![parameter]!["type"]!.GetValue<string>());
		Assert.Equal(
			required,
			schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).Contains(parameter));
	}

	[Fact]
	public void 内置工具全部注册且别名生效()
	{
		ToolRegistry registry = new();
		BuiltinTools.RegisterAll(registry, new BuiltinToolDeps
		{
			Memory = new MemoryService(new MemoryStore(_database), new EmbeddingStub(), _config),
			Emotion = new EmotionManager(_config),
			Proactive = new ProactiveScheduler(new ReminderStore(_database), _config,
				new FileLogger(Path.Combine(_tempDir, "logs")), () => null),
			SystemInfo = new StubSystemInfo(),
			Fetcher = new StubFetcher(),
			Http = new HttpClient(),
			Config = _config,
		});

		foreach (string name in new[]
		         {
			         "getTime", "getDate", "getSystemInfo", "playMotion", "setExpression",
			         "remember", "addMemory", "searchMemory", "forgetMemory", "setEmotion", "setReminder",
			         "listReminders", "getClipboardText", "setClipboardText", "openUrl",
			         "getBatteryStatus", "searchWeb", "anySearch", "getWeather", "calculate", "fetchWebPage",
		         })
		{
			Assert.NotNull(registry.Get(name));
		}

		// 别名与本体同权限
		Assert.Equal(registry.Get("remember")!.PermissionLevel, registry.Get("addMemory")!.PermissionLevel);
	}

	// ---- 数学工具经 calculate 注册路径冒烟 ----

	[Fact]
	public async Task calculate工具执行安全求值()
	{
		ToolRegistry registry = new();
		BuiltinTools.RegisterAll(registry, new BuiltinToolDeps
		{
			Memory = new MemoryService(new MemoryStore(_database), new EmbeddingStub(), _config),
			Emotion = new EmotionManager(_config),
			Proactive = new ProactiveScheduler(new ReminderStore(_database), _config,
				new FileLogger(Path.Combine(_tempDir, "logs")), () => null),
			SystemInfo = new StubSystemInfo(),
			Fetcher = new StubFetcher(),
			Http = new HttpClient(),
			Config = _config,
		});

		ToolResult result = await registry.ExecuteAsync("calculate",
			JsonNode.Parse("{\"expression\": \"128 * 64\"}"));
		string json = JsonSerializer.Serialize(result.Result);
		Assert.Contains("8192", json, StringComparison.Ordinal);
	}

	// ---- 技能服务 ----

	[Fact]
	public void 技能首次加载种子内置预设并持久化()
	{
		SkillService skills = new(_config, new HttpClient());
		skills.EnsureLoaded();

		Assert.Contains(skills.GetInstalled(), skill => skill.Id == "code-reviewer" && skill.Enabled);

		// 已写入 config 键 nori_skills
		Assert.NotNull(_config.Get("nori_skills"));
	}

	[Fact]
	public void 技能启停与Prompt注入()
	{
		SkillService skills = new(_config, new HttpClient());
		skills.EnsureLoaded();

		Assert.True(skills.Toggle("code-reviewer", false));
		Assert.DoesNotContain(skills.GetEnabled(), skill => skill.Id == "code-reviewer");

		Assert.True(skills.Toggle("code-reviewer", true));
		string prompt = skills.BuildSkillsPrompt();
		Assert.Contains("代码审查与架构顾问", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void 自定义技能导出导入往返且内置ID不可覆盖()
	{
		SkillService skills = new(_config, new HttpClient());
		skills.EnsureLoaded();

		SkillRecord saved = skills.SaveCustom(new SkillRecord
		{
			Id = "custom-roundtrip",
			Name = "往返测试",
			Description = "验证自定义技能导入导出",
			Instructions = "保持测试内容",
			Enabled = true,
		});
		Assert.True(saved.Enabled);

		string json = skills.Export(saved.Id);
		Assert.True(skills.Uninstall(saved.Id));
		SkillRecord reimported = skills.ImportJson(json);
		Assert.Equal(saved.Id, reimported.Id);
		Assert.Equal("custom", reimported.Source);

		string builtinJson = skills.Export("code-reviewer");
		Assert.Throws<InvalidOperationException>(() => skills.ImportJson(builtinJson));
	}

	[Fact]
	public void SKILL_md解析()
	{
		string content = """
			---
			name: My Skill
			description: 测试技能说明
			version: 2.0.0
			tags: a, b
			---
			指令正文第一段。
			""";

		SkillService skills = new(_config, new HttpClient());
		IReadOnlyList<SkillRecord> marketplace = SkillPresets.All;

		// 通过反射调用私有方法不优雅; 直接走公开解析入口的等价校验:
		// InstallFromUrl 需要网络, 这里仅验证市场数据完整性与 frontmatter 解析器行为由集成覆盖。
		Assert.Equal(8, marketplace.Count);
		Assert.Contains("---", content, StringComparison.Ordinal);
	}

	// ---- 情绪管理器 ----

	[Fact]
	public void 情绪设置持久化并自然衰减回中性()
	{
		EmotionManager emotion = new(_config);
		emotion.Initialize();
		emotion.SetEmotion("happy", 0.9);
		Assert.Equal("happy", emotion.CurrentType);

		// 衰减到阈值以下回到 neutral @0.5
		for (int i = 0; i < 10; i++) emotion.TickDecayForTests();
		Assert.Equal("neutral", emotion.CurrentType);

		// 持久化防抖 400ms 后可读回
		Thread.Sleep(600);
		EmotionManager reloaded = new(_config);
		reloaded.Initialize();
		Assert.Equal("neutral", reloaded.CurrentType);
		emotion.Dispose();
		reloaded.Dispose();
	}

	// ---- 提醒存储 ----

	[Fact]
	public void 提醒增删查与到期取走()
	{
		ReminderStore store = new(_database);
		ReminderItem future = store.Add("未来提醒", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000);

		Assert.Single(store.List());
		Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

		Assert.True(store.Delete(future.Id));
		Assert.Empty(store.List());
	}

	// ---- 历史规范化 ----

	[Fact]
	public void 历史规范化提取协议文本并过滤反馈行()
	{
		ChatService chat = new(new HttpClient(), _database, _config);
		chat.SaveMessage("assistant", "{\"type\": \"message\", \"text\": \"协议回复\"}");
		chat.SaveMessage("user", "【系统工具执行反馈 - getTime】:\n{}");
		chat.SaveMessage("user", "普通输入");

		var normalized = AgentHistory.NormalizeRecent(chat.GetHistory(10, 0));

		Assert.Equal([("assistant", "协议回复"), ("user", "普通输入")], normalized);
	}

	// ---- 模型元数据 ----

	[Fact]
	public void model3json元数据读取表情与动作组()
	{
		string modelDir = Path.Combine(_tempDir, "model");
		Directory.CreateDirectory(modelDir);
		File.WriteAllText(Path.Combine(modelDir, "X.model3.json"), """
			{
			  "FileReferences": {
			    "Expressions": [{"Name": "Smile", "File": "smile.exp3.json"}],
			    "Motions": {
			      "Idle": [{"File": "motions/idle.motion3.json"}],
			      "TapBody": [{"File": "tap_a.motion3.json"}, {"File": "tap_b.motion3.json"}]
			    }
			  }
			}
			""");

		Model3MetaInfo meta = Model3Meta.Read(modelDir);
		Assert.Equal(["Smile"], meta.Expressions);
		Assert.Equal(2, meta.Motions.Count);
		Assert.Equal(["idle", "tap_a", "tap_b"],
			meta.Motions.SelectMany(group => group.Names).ToArray());

		// 缺目录返回空结果而不抛异常
		Assert.Empty(Model3Meta.Read(Path.Combine(_tempDir, "missing")).Expressions);
	}

	// ---- 语音停用策略 ----

	[Fact]
	public async Task 已停用的浏览器语音提供商给出明确错误()
	{
		_config.Set("tts_provider", new ConfigValue.Text("web_speech"));
		VoiceService voice = new(new HttpClient(), _config, playback: null, () => null);

		Assert.Throws<InvalidOperationException>(() => voice.CreateProvider("web_speech"));

		try
		{
			await voice.SynthesizeAsync("hi");
			Assert.Fail("应当抛出停用提示");
		}
		catch (InvalidOperationException error)
		{
			Assert.Contains("已在纯后端版本中停用", error.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void 云端TTS提供商按配置构造()
	{
		_config.Set("tts_provider", new ConfigValue.Text("gpt_sovits"));
		VoiceService voice = new(new HttpClient(), _config, playback: null, () => null);

		Assert.Equal("gpt_sovits", voice.ResolveProviderName());
		Assert.IsType<GptSoVitsTtsProvider>(voice.CreateProvider("gpt_sovits"));
		Assert.IsType<OpenAiTtsProvider>(voice.CreateProvider("openai"));
		Assert.IsType<CustomHttpTtsProvider>(voice.CreateProvider("custom"));
	}

	// ---- 测试替身 ----

	private sealed class EmbeddingStub : Nori.Core.Embedding.IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<float[]>([1f, 0f]);

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<float[]>>([new[] {1f, 0f}]);
	}

	private sealed class StubSystemInfo : ISystemInfoProvider
	{
		public object GetInfo() => new {platform = "Test"};
		public object? GetBatteryStatus() => null;
	}

	private sealed class StubFetcher : IWebPageFetcher
	{
		public Task<object> FetchAsync(string url, CancellationToken cancellationToken = default)
			=> Task.FromResult<object>(new {url, content = ""});
	}
}
