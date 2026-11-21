using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	// ---- 文件访问设置页（工作目录 + 工具轮数）----

	[Fact]
	public async Task 设置工作目录之后文件工具才出现()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();

		// 没配之前一件都没有 —— 让模型看见一件永远失败的工具只会让它反复试。
		Assert.Null(_runtime.Tools.Get("readFile"));

		await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_workspace", Args(new { root = folder }));

		Assert.NotNull(_runtime.Tools.Get("readFile"));
		Assert.NotNull(_runtime.Tools.Get("writeFile"));
	}

	/// <summary>
	/// 清空之后必须真的消失。
	///
	/// 工具在注册时把工作目录焊进了闭包，只靠「不再注册」的话旧工具还挂着、还指着旧目录 ——
	/// 用户在界面上清空了，她却照样读得到，这种不一致很难查。
	/// </summary>
	[Fact]
	public async Task 清空工作目录之后文件工具消失()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));
		Assert.NotNull(_runtime.Tools.Get("readFile"));

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = "" }));

		Assert.Null(_runtime.Tools.Get("readFile"));
		Assert.Null(_runtime.Tools.Get("writeFile"));
	}

	/// <summary>
	/// 存一个不存在的路径不会报错，但那一族工具会静默不注册 —— 用户看到自己填了值、她却说
	/// 「我看不到文件」。就地拒绝比事后排查便宜得多。
	/// </summary>
	[Fact]
	public async Task 不存在的文件夹被就地拒绝()
	{
		BridgeCommands commands = CreateCommands();

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main),
				"settings_update_workspace",
				Args(new { root = Path.Combine(_tempDir, "并不存在") })));

		Assert.Contains("不存在", error.Message, StringComparison.Ordinal);
		Assert.Equal("", _config.GetStringOr(ConfigStore.KeyWorkspaceRoot, ""));
	}

	[Fact]
	public async Task 工具轮数存成整数并被夹回范围()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 20 }));
		Assert.Equal(20, _runtime.Engine.ConfiguredToolIterations);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 999 }));
		Assert.Equal(Nori.Core.Agent.AgentEngine.MaxToolIterationsLimit, _runtime.Engine.ConfiguredToolIterations);

		// 存的是 Integer 而不是 Text：文本那条路上 "0"/"1" 会被解析成布尔再渲染成 "false"/"true"。
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 1 }));
		Assert.Equal(1, _runtime.Engine.ConfiguredToolIterations);
	}

	[Fact]
	public async Task 快照把工作目录与轮数报给界面()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"settings_update_workspace",
			Args(new { root = folder, maxToolIterations = 9 }));

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement workspace = snapshot.GetProperty("workspace");

		Assert.Equal(folder, workspace.GetProperty("root").GetString());
		Assert.True(workspace.GetProperty("available").GetBoolean());
		Assert.Equal(9, workspace.GetProperty("maxToolIterations").GetInt32());
	}

	/// <summary>目录被删掉之后配置还在，但工具已经不注册了 —— 界面要能说出这个差别。</summary>
	[Fact]
	public async Task 目录事后被删掉时快照报告不可用()
	{
		string folder = Path.Combine(_tempDir, "会被删掉");
		Directory.CreateDirectory(folder);
		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_workspace", Args(new { root = folder }));

		Directory.Delete(folder);
		_runtime.InvalidateSnapshot();

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement workspace = snapshot.GetProperty("workspace");

		Assert.Equal(folder, workspace.GetProperty("root").GetString());
		Assert.False(workspace.GetProperty("available").GetBoolean());
	}

	// ---- 情绪表达 ----

	/// <summary>
	/// 通道键必须带前缀。
	///
	/// 这条命令拿键名直写配置，不挡的话它就成了「改任意配置项」的通用入口。
	/// </summary>
	[Theory]
	[InlineData("agent_max_tool_iterations")]
	[InlineData("workspace_root")]
	[InlineData("")]
	public async Task 非表达通道的键被拒绝(string key)
	{
		BridgeCommands commands = CreateCommands();

		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"settings_update_expression",
			Args(new {channel = key, enabled = true})));
	}

	[Fact]
	public async Task 开关表达通道会落库()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(
			main, "settings_update_expression", Args(new {channel = "expression_tray_icon", enabled = false}));

		Assert.False(_config.GetBoolOr("expression_tray_icon", true));
	}

	/// <summary>快照要同时给出「开没开」与「能不能用」—— 只给一个的话「开了没反应」无从排查。</summary>
	[Fact]
	public void 快照按通道报出开关与可用性()
	{
		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement expression = snapshot.GetProperty("expression");

		JsonElement tray = expression.GetProperty("expression_tray_icon");
		Assert.True(tray.TryGetProperty("enabled", out _));
		Assert.True(tray.TryGetProperty("available", out _));

		// 改整个系统那一条默认关。
		Assert.False(expression.GetProperty("expression_accent_color").GetProperty("enabled").GetBoolean());

		// 她自己身上那两条默认开。
		Assert.True(expression.GetProperty("expression_tray_icon").GetProperty("enabled").GetBoolean());
	}
	[Fact]
	public async Task 首启与主界面都可更新AI设置但密钥只写不读()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource("first-run"), "settings_update_ai_providers", Args(new
		{
			baseUrl = "https://api.example.com/v1",
			apiKey = "sk-new", // nosemgrep
			model = "gpt-x",
		}));
		Assert.Equal("sk-new", _config.GetStringOr("llm_api_key", ""));

		// 显式空串清除密钥
		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_ai_providers", Args(new {apiKey = ""})); // nosemgrep
		Assert.False(_config.Exists("llm_api_key"));
	}

	[Fact]
	public async Task AI设置插件提供商按读取规则校验并保存去空白ID()
	{
		BridgeCommands commands = CreateCommands();
		foreach (string forged in new[] {" plugin:missing.plugin:mock", "Plugin:missing.plugin:mock"})
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new {chat = new {provider = forged}})));
			Assert.False(_config.Exists(Nori.Core.Configuration.AiSettingsStore.KeyLlmProvider));
		}

		_services.Chat.CustomProviders.Register(
			new Nori.Core.Chat.CustomChatProviderDescriptor("plugin:local.plugin:mock", "Mock", "", false),
			(_, _, _) => throw new InvalidOperationException("not used"),
			(_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers",
			Args(new {chat = new {provider = "  plugin:local.plugin:mock  "}}));
		Assert.Equal("plugin:local.plugin:mock", _services.AiSettings.Read().Chat.ProviderId);
	}

	[Fact]
	public async Task AI设置更新思考档位上下文与图像生成()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new
		{
			chat = new {reasoning = "high"},
			context = new {limit = "64k", compaction = false},
			image = new {provider = "google", model = "gemini-2.5-flash-image", apiKey = "img-secret", size = "16:9"}, // nosemgrep
		}));

		AiProviderSettings settings = _services.AiSettings.Read();
		Assert.Equal(Nori.Core.Chat.LlmReasoningLevel.High, settings.Chat.Reasoning);
		Assert.Equal("64000", _config.GetStringOr(Nori.Core.Agent.ContextWindowPlanner.KeyContextLimit, ""));
		Assert.False(_config.GetBoolOr(Nori.Core.Agent.ConversationCompactor.KeyEnabled, true));
		Assert.Equal("google", settings.Image.Provider);
		Assert.Equal("img-secret", settings.Image.ApiKey);
		Assert.Equal("16:9", settings.Image.Size);

		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new {context = new {limit = "auto"}}));
		Assert.False(_config.Exists(Nori.Core.Agent.ContextWindowPlanner.KeyContextLimit));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new {context = new {limit = "abc"}})));
	}

	[Fact]
	public async Task 统一AI设置更新保持聊天与Embedding独立()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new
		{
			baseUrl = "https://chat.example/v1",
			apiKey = "chat-secret", // nosemgrep
			model = "chat-model",
			embedding = new
			{
				baseUrl = "http://localhost:11434/v1",
				model = "local-embedding",
				apiKey = "", // nosemgrep
			},
		}));

		AiProviderSettings settings = _services.AiSettings.Read();
		Assert.Equal("https://chat.example/v1", settings.Chat.BaseUrl);
		Assert.Equal("chat-secret", settings.Chat.ApiKey);
		Assert.Equal("http://localhost:11434/v1", settings.Embedding.BaseUrl);
		Assert.Equal("local-embedding", settings.Embedding.Model);
		Assert.Empty(settings.Embedding.ApiKey);
		Assert.True(settings.Embedding.IsConfigured);
	}

	[Fact]
	public async Task 新统一AI命令接受嵌套聊天与Embedding补丁()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new
		{
			chat = new
			{
				baseUrl = "https://chat.example/v1",
				model = "chat-model",
			},
			persona = "保持简洁",
			embedding = new
			{
				baseUrl = "http://127.0.0.1:11434/v1",
				model = "nomic-embed-text",
				dimensions = "768",
			},
		}));

		AiProviderSettings settings = _services.AiSettings.Read();
		Assert.Equal("https://chat.example/v1", settings.Chat.BaseUrl);
		Assert.Equal("chat-model", settings.Chat.Model);
		Assert.Equal("保持简洁", settings.Chat.Persona);
		Assert.Equal("http://127.0.0.1:11434/v1", settings.Embedding.BaseUrl);
		Assert.Equal("nomic-embed-text", settings.Embedding.Model);
		Assert.Equal(768, settings.Embedding.Dimensions);
	}
	[Theory]
	[InlineData("run_gc_collect")]
	[InlineData("debug_crash_test")]
	[InlineData("write_log")]
	public async Task RemovedDiagnosticCommandsAreUnknown(string command)
	{
		FakeBridgeSource main = new(WindowLabels.Main);
		JsonElement args = Args(new { });
		InvalidOperationException direct = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(main, command, args));
		Assert.Equal($"未知的命令: {command}", direct.Message);
	}
}
