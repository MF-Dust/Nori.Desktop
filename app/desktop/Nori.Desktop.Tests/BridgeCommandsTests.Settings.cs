using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
using Nori.Desktop.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.AvaloniaTheme.MacOS;

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

	[Fact]
	public async Task 设置窗口可以执行这两条命令()
	{
		// 原生设置页不走 WebView invoke，命令必须在 SettingsService 的白名单里，否则界面上
		// 的按钮点了会报「不允许执行」。
		Assert.Contains("settings_update_workspace", SettingsService.Commands);
		Assert.Contains("settings_pick_workspace", SettingsService.Commands);
		Assert.Contains("settings_update_tasks", SettingsService.Commands);
		await Task.CompletedTask;
	}
	// ---- 情绪表达 ----

	[Fact]
	public async Task 设置窗口可以开关表达通道()
	{
		Assert.Contains("settings_update_expression", SettingsService.Commands);
		await Task.CompletedTask;
	}

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
	[Fact]
	public Task NativeSettingsServiceUsesSharedSnapshotAndStateNotification() => WithSettingsUiAsync(async () =>
	{
		using SettingsService settings = new(_services, new Window());
		int stateChanged = 0;
		settings.StateChanged += () => stateChanged++;

		await settings.ExecuteAsync("settings_update_general", new {autoCheckUpdates = false});
		Assert.Equal("false", _config.GetStringOr("auto_check_updates", "true"));
		Assert.True(stateChanged > 0);

		JsonElement snapshot = await settings.GetSnapshotAsync();
		Assert.False(snapshot.GetProperty("general").GetProperty("autoCheckUpdates").GetBoolean());
	});

	[Fact]
	public Task NativeMcpReadDoesNotTriggerAnotherRefresh() => WithSettingsUiAsync(async () =>
	{
		Window window = new();
		using SettingsService settings = new(_services, window);
		int changes = 0;
		settings.StateChanged += () => changes++;
		try
		{
			window.Show();
			await settings.ExecuteAsync("mcp_get_servers");
			Assert.Equal(0, changes);
		}
		finally { window.Close(); }
	});

	[Fact]
	public Task NativeSettingsServiceRejectsCommandsOutsideSettingsPolicy() => WithSettingsUiAsync(async () =>
	{
		using SettingsService settings = new(_services, new Window());

		await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ExecuteAsync("chat_start", new {text = "不能从设置窗口发起聊天"}));
		await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ExecuteAsync("window_open_settings", new {page = "ai"}));
	});

	[Fact]
	public Task NativeSettingsThemeInitializesControlTemplates() => WithSettingsUiAsync(() =>
	{
		DevolutionsMacOsTheme theme = Assert.IsType<DevolutionsMacOsTheme>(Assert.Single(Application.Current!.Styles));
		Assert.NotEmpty(theme);
		Button button = new() {Content = "测试"};
		TextBox input = new();
		Window window = new() {Content = new StackPanel {Children = {button, input}}};
		try
		{
			window.Show();
			window.UpdateLayout();
			Assert.NotNull(button.Template);
			Assert.NotNull(input.Template);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task NativeSettingsWindowRefreshesSnapshotOnUiThread(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
		using SettingsService service = new(fixture._services, new Window());
		using SettingsViewModel viewModel = new(service);
		SettingsWindow window = new() {DataContext = viewModel, Width = width, Height = height};
		try
		{
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			window.UpdateLayout();
			Assert.Equal(string.Empty, viewModel.ErrorMessage);
			Assert.Equal("en-US", viewModel.Language);
			SettingsPagePresenter presenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
			Assert.IsType<StackPanel>(presenter.Content);
			Assert.True(presenter.Bounds.Width > 0);
			Assert.True(presenter.Bounds.Height > 0);
			// 状态通知来自后台线程，也必须走同一条 UI 刷新路径。
			await Task.Run(() => viewModel.RefreshSnapshotAsync());
			Assert.Equal(string.Empty, viewModel.ErrorMessage);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	[Fact]
	public Task NativeSettingsPagesSurviveAttachmentAndNavigation() => WithSettingsUiAsync(async () =>
	{
		Window window = new() {Width = 720, Height = 480};
		using SettingsService service = new(_services, window);
		using SettingsViewModel viewModel = new(service);
		SettingsPagePresenter presenter = new();
		try
		{
			window.Show();
			foreach (string key in new[] {"ai", "skills", "mcp", "automation", "plugins", "debug", "general", "ai"})
			{
				window.Content = null;
				viewModel.Navigate(key);
				await viewModel.RefreshSnapshotAsync();
				presenter.DataContext = viewModel.CurrentPage;
				object? content = presenter.Content;
				window.Content = presenter;
				window.UpdateLayout();
				if (viewModel.CurrentPage is NativeSettingsPageBase)
				{
					Assert.IsType<NativeSettingsPagePresenter>(presenter.Content);
					Assert.Same(content, presenter.Content);
				}
				else
				{
					StackPanel form = Assert.IsType<StackPanel>(presenter.Content);
					Assert.Contains(form.Children, child => child is Border);
				}
				Assert.True(presenter.Bounds.Width > 0);
				Assert.True(presenter.Bounds.Height > 0);
				presenter.RefreshPage();
			}
		}
		finally
		{
			presenter.DataContext = null;
			window.Close();
		}
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task NativeDiagnosticsRefreshPreservesControlsAndScroll(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		for (int index = 0; index < 80; index++)
			fixture._services.Logger.Write(LogSource.Backend, "warn", $"诊断滚动回归日志 {index:D3}");
		SettingsWindow window = new() {Width = width, Height = height};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			viewModel.Navigate("debug");
			await viewModel.RefreshSnapshotAsync();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.Empty(viewModel.ErrorMessage);
			SettingsPagePresenter pagePresenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
			NativeSettingsPagePresenter presenter = Assert.IsType<NativeSettingsPagePresenter>(pagePresenter.Content);
			Control root = Assert.IsAssignableFrom<Control>(presenter.Content);
			ComboBox filter = root.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Name == "DebugLevelFilter");
			ListBox logList = root.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "DebugLogList");
			ScrollViewer logs = logList.GetVisualDescendants().OfType<ScrollViewer>().Single();
			ScrollViewer pageScroll = pagePresenter.GetVisualAncestors().OfType<ScrollViewer>().First();
			filter.SelectedIndex = 2;
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.True(filter.Focus());
			logs.Offset = new Vector(0, 100);
			window.UpdateLayout();
			Assert.True(logs.Offset.Y > 0);
			Vector logOffset = logs.Offset;
			Vector pageOffset = pageScroll.Offset;
			object? logContent = logs.Content;
			ScrollBar[] bars = pageScroll.GetVisualDescendants().OfType<ScrollBar>().ToArray();
			Assert.NotEmpty(bars);
			for (int iteration = 0; iteration < 3; iteration++)
			{
				await Task.WhenAll(viewModel.RefreshSnapshotAsync(), viewModel.RefreshSnapshotAsync());
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
				Assert.Empty(viewModel.ErrorMessage);
				Assert.Same(root, presenter.Content);
				Assert.Same(filter, root.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Name == "DebugLevelFilter"));
				Assert.Same(logs, logList.GetVisualDescendants().OfType<ScrollViewer>().Single());
				Assert.Same(logContent, logs.Content);
				Assert.Equal(2, filter.SelectedIndex);
				Assert.True(filter.IsFocused);
				Assert.Equal(logOffset, logs.Offset);
				Assert.Equal(pageOffset, pageScroll.Offset);
				Assert.Equal(bars, pageScroll.GetVisualDescendants().OfType<ScrollBar>().ToArray());
			}
		}
		finally
		{
			window.DataContext = null;
			window.Close();
		}
	});

	[Fact]
	public Task NativeSettingsRefreshDoesNotLoadHiddenComplexPages() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window window = new();
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		NativeSettingsPageBase[] hidden = viewModel.Groups.SelectMany(group => group.Pages)
			.Select(item => item.Page).OfType<NativeSettingsPageBase>().Where(page => page.Key != "debug").ToArray();
		List<string> refreshed = [];
		foreach (NativeSettingsPageBase page in hidden)
			page.ComplexViewModel.PropertyChanged += (_, args) =>
			{
				if (args.PropertyName == nameof(SettingsPageViewModelBase.IsBusy) && page.ComplexViewModel.IsBusy)
					refreshed.Add(page.Key);
			};
		try
		{
			window.Show();
			viewModel.Navigate("debug");
			for (int iteration = 0; iteration < 3; iteration++) await viewModel.RefreshSnapshotAsync();
			Assert.Empty(viewModel.ErrorMessage);
			Assert.Empty(refreshed);
			viewModel.Navigate("mcp");
			await viewModel.RefreshSnapshotAsync();
			Assert.Empty(viewModel.ErrorMessage);
			Assert.Contains("mcp", refreshed);
			Assert.All(refreshed, key => Assert.Equal("mcp", key));
			Assert.Equal("mcp", Assert.Single(viewModel.Groups.SelectMany(group => group.Pages), item => item.IsSelected).Key);
		}
		finally { window.Close(); }
	});
	[Fact]
	public async Task SettingsUpdateGeneral_UpdatesAutoCheckUpdates()
	{
		BridgeCommands commands = CreateCommands();

		// 更新 autoCheckUpdates 为 false
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", Args(new { autoCheckUpdates = false }));
		Assert.Equal("false", _config.GetStringOr("auto_check_updates", "true"));

		// 验证快照反映该值
		object snapshot = _runtime.BuildSnapshot();
		string snapshotJson = JsonSerializer.Serialize(snapshot);
		using JsonDocument doc = JsonDocument.Parse(snapshotJson);
		Assert.False(doc.RootElement.GetProperty("general").GetProperty("autoCheckUpdates").GetBoolean());

		// 重新开启
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", Args(new { autoCheckUpdates = true }));
		Assert.Equal("true", _config.GetStringOr("auto_check_updates", "false"));
	}
}
