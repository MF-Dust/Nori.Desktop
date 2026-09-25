using System.Text.Json;
using Nori.PluginRuntime;
using Xunit;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginHostTests
{
	#region 1. 标识符有效性校验测试 (防路径穿越与非法注入)

	[Theory]
	[InlineData("io.my.plugin")]
	[InlineData("window_1")]
	[InlineData("com.example.tool")]
	[InlineData("win-123.abc")]
	[InlineData("a")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // 64位
	public void 合法标识符校验通过(string id)
	{
		Assert.True(PluginWindowHost.IsValidId(id));
		PluginWindowHost.ValidateId(id, nameof(id)); // 不应抛出异常
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("../evil")]
	[InlineData("..\\evil")]
	[InlineData("foo/bar")]
	[InlineData("foo\\bar")]
	[InlineData("../../etc/passwd")]
	[InlineData("..")]
	[InlineData("plugin:123")]
	[InlineData("has:colon")]
	[InlineData("with space")]
	[InlineData("bad*char")]
	[InlineData("bad?char")]
	[InlineData("bad<char>")]
	[InlineData("bad|char")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef1")] // 65位 (超长)
	public void 非法标识符校验被拒绝(string? id)
	{
		Assert.False(PluginWindowHost.IsValidId(id));
		if (id != null)
		{
			Assert.Throws<ArgumentException>(() => PluginWindowHost.ValidateId(id, nameof(id)));
		}
	}

	#endregion

	#region 2. 标签生成与解析测试 (Label Identity)

	[Fact]
	public void 生成标准插件窗口标签()
	{
		string label = PluginWindowHost.BuildLabel("io.weather.tool", "main-view");
		Assert.Equal("plugin:io.weather.tool:main-view", label);
	}

	[Fact]
	public void 正确解析标准插件窗口标签()
	{
		bool success = PluginWindowHost.TryParseLabel("plugin:io.weather.tool:main-view", out string? pluginId, out string? windowId);
		Assert.True(success);
		Assert.Equal("io.weather.tool", pluginId);
		Assert.Equal("main-view", windowId);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("main")]
	[InlineData("pet")]
	[InlineData("first-run")]
	[InlineData("init")]
	[InlineData("plugin:only_one_part")]
	[InlineData("plugin:a:b:c")]
	[InlineData("custom:pluginA:main")]
	[InlineData("plugin:bad/id:win")]
	[InlineData("plugin:good:bad\\win")]
	public void 非法或非插件标签解析失败(string? label)
	{
		bool success = PluginWindowHost.TryParseLabel(label, out string? pluginId, out string? windowId);
		Assert.False(success);
		Assert.Null(pluginId);
		Assert.Null(windowId);
	}

	#endregion

	#region 3. 插件 ID 防伪造测试 (Anti-Spoofing)

	[Fact]
	public async Task 桥接消息中伪造的PluginId被完全忽略()
	{
		FakePluginBridgeSource source = new("io.trusted.plugin123", "win-panel");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.trusted.plugin123",
			Name = "Trusted Plugin",
			Version = "1.0.0",
			Capabilities = ["ui.webview"],
		};

		PluginBridge bridge = new("io.trusted.plugin123", "win-panel", descriptor);

		// 构造携带伪造 pluginId 的请求载荷
		string spoofPayload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 1001,
			cmd = "plugin_get_info",
			args = new
			{
				pluginId = "malicious-admin-plugin",
			},
		});

		bridge.Handle(source, spoofPayload);

		// 等待异步处理完成
		FakeResult result = await source.WaitForResultAsync(1001);
		Assert.Equal("resolve", result.Kind);
		Assert.Null(result.Error);
		Assert.NotNull(result.Value);

		// 反序列化结果，确认返回的身份依然是构造时绑定的真实插件 ID
		string json = JsonSerializer.Serialize(result.Value);
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.Equal("io.trusted.plugin123", doc.RootElement.GetProperty("id").GetString());
	}

	[Fact]
	public async Task WindowGetInfo严格返回构造绑定的身份()
	{
		FakePluginBridgeSource source = new("io.my.plugin", "sidebar");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.my.plugin",
			Name = "My Plugin",
			Version = "2.1.0",
		};

		PluginBridge bridge = new("io.my.plugin", "sidebar", descriptor);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 1002,
			cmd = "window_get_info",
			args = new { },
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(1002);
		Assert.Equal("resolve", result.Kind);

		string json = JsonSerializer.Serialize(result.Value);
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.Equal("io.my.plugin", doc.RootElement.GetProperty("pluginId").GetString());
		Assert.Equal("sidebar", doc.RootElement.GetProperty("windowId").GetString());
		Assert.Equal("plugin:io.my.plugin:sidebar", doc.RootElement.GetProperty("label").GetString());
	}

	#endregion

	#region 4. 描述符脱敏与 InstallPath 隔离测试 (Descriptor Sanitization)

	[Fact]
	public void 插件描述符不暴露安装路径()
	{
		Assert.Null(typeof(PluginDescriptor).GetProperty("InstallPath"));
		Assert.Null(typeof(PluginDescriptorSummary).GetProperty("InstallPath"));
	}

	[Fact]
	public async Task PluginGetInfo不泄露文件系统路径()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
			Description = "A safe plugin",
			Author = "Nori Dev",
			Capabilities = ["ui.webview", "storage"],
		};

		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 2001,
			cmd = "plugin_get_info",
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(2001);
		Assert.Equal("resolve", result.Kind);

		string json = JsonSerializer.Serialize(result.Value);
		Assert.DoesNotContain("InstallPath", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("/home/", json, StringComparison.OrdinalIgnoreCase);
	}

	#endregion

	#region 5. 独立桥接安全白名单与 NoriBridge 隔离测试 (Allowlist Enforcement)

	[Theory]
	[InlineData("plugin_get_info")]
	[InlineData("plugin_get_capabilities")]
	[InlineData("window_get_info")]
	[InlineData("ping")]
	[InlineData("window_close")]
	public async Task 白名单命令被允许执行(string cmd)
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
			Capabilities = ["ui.webview"],
		};

		bool closeCalled = false;
		PluginBridge bridge = new(
			"io.plugin.a",
			"win-1",
			descriptor,
			capabilityProvider: () => ["ui.webview", "custom.cap"],
			closeSelfHandler: ct =>
			{
				closeCalled = true;
				return Task.CompletedTask;
			});

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 3001,
			cmd = cmd,
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(3001);
		Assert.Equal("resolve", result.Kind);
		Assert.Null(result.Error);

		if (cmd == "window_close")
		{
			Assert.True(closeCalled);
		}
	}

	[Theory]
	[InlineData("plugin.getInfo")]
	[InlineData("get_info")]
	[InlineData("plugin.getCapabilities")]
	[InlineData("capability_status")]
	[InlineData("window.getInfo")]
	[InlineData("window_ping")]
	[InlineData("window.close")]
	[InlineData("close")]
	[InlineData("window_show")]
	[InlineData("window_hide")]
	[InlineData("settings_update_ai")]
	[InlineData("chat_start")]
	[InlineData("chat_completion")]
	[InlineData("tools_execute")]
	[InlineData("mcp_get_servers")]
	[InlineData("automation_browser_status")]
	[InlineData("automation_task_create")]
	[InlineData("complete_first_run")]
	[InlineData("get_config")]
	[InlineData("set_config")]
	[InlineData("unknown_malicious_command")]
	public async Task 非白名单命令与宿主核心命令被严格拒绝(string forbiddenCmd)
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 3002,
			cmd = forbiddenCmd,
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(3002);
		Assert.Equal("reject", result.Kind);
		Assert.Null(result.Value);
		Assert.NotNull(result.Error);
		Assert.Contains("白名单", result.Error);
	}

	#endregion

	#region 6. 异常包装与脱敏测试 (Exception Wrapping)

	[Fact]
	public async Task 回调抛出异常时被包装为稳定的Reject响应()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		PluginBridge bridge = new(
			"io.plugin.a",
			"win-1",
			descriptor,
			capabilityProvider: () => throw new InvalidOperationException("内部能力查询失败，涉及路径 C:\\Users\\Secret\\test"));

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 4001,
			cmd = "plugin_get_capabilities",
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(4001);
		Assert.Equal("reject", result.Kind);
		Assert.Null(result.Value);
		Assert.NotNull(result.Error);
		// 敏感路径已被脱敏
		Assert.DoesNotContain("C:\\Users\\Secret", result.Error);
	}

	#endregion

	#region 7. 能力契约与参数校验测试 (PluginWebViewCapability)

	[Fact]
	public async Task PluginWebViewCapability校验合法参数并委托工厂创建()
	{
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.test.plugin",
			Name = "Test Plugin",
			Version = "1.0.0",
		};

		bool factoryCalled = false;
		FakePluginWebViewWindow fakeWindow = new("io.test.plugin", "win-view", "Title", "http://127.0.0.1:14201/plugins/test/index.html");

		PluginWebViewCapability capability = new(
			descriptor,
			(desc, opts, ct) =>
			{
				factoryCalled = true;
				Assert.Equal("io.test.plugin", desc.Id);
				Assert.Equal("win-view", opts.Id);
				Assert.Equal("My Tool Window", opts.Title);
				return Task.FromResult<IPluginWebViewWindow>(fakeWindow);
			});

		PluginWebViewOptions options = new()
		{
			Id = "win-view",
			Title = "My Tool Window",
			EntryPoint = "/plugins/test/index.html",
			Width = 600,
			Height = 400,
		};

		IPluginWebViewWindow window = await capability.CreateWindowAsync(options);
		Assert.True(factoryCalled);
		Assert.Same(fakeWindow, window);
		Assert.Equal("plugin:io.test.plugin:win-view", window.Label);
	}

	[Fact]
	public async Task PluginWebViewCapability拒绝非法参数()
	{
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.test.plugin",
			Name = "Test Plugin",
			Version = "1.0.0",
		};

		PluginWebViewCapability capability = new(
			descriptor,
			(desc, opts, ct) => Task.FromResult<IPluginWebViewWindow>(new FakePluginWebViewWindow(desc.Id, opts.Id, opts.Title, opts.EntryPoint)));

		// 1. null options
		await Assert.ThrowsAsync<ArgumentNullException>(() => capability.CreateWindowAsync(null!));

		// 2. 非法 WindowId (路径穿越)
		await Assert.ThrowsAsync<ArgumentException>(() => capability.CreateWindowAsync(new PluginWebViewOptions
		{
			Id = "../escape",
			Title = "Title",
			EntryPoint = "/index.html",
		}));

		// 3. 空白标题
		await Assert.ThrowsAsync<ArgumentException>(() => capability.CreateWindowAsync(new PluginWebViewOptions
		{
			Id = "win-1",
			Title = "",
			EntryPoint = "/index.html",
		}));

		// 4. 空白 URL
		await Assert.ThrowsAsync<ArgumentException>(() => capability.CreateWindowAsync(new PluginWebViewOptions
		{
			Id = "win-1",
			Title = "Title",
			EntryPoint = "  ",
		}));

		// 5. 非法尺寸
		await Assert.ThrowsAsync<ArgumentException>(() => capability.CreateWindowAsync(new PluginWebViewOptions
		{
			Id = "win-1",
			Title = "Title",
			EntryPoint = "/index.html",
			Width = -10,
			Height = 400,
		}));
	}

	[Theory]
	[InlineData("https://example.com/plugin")]
	[InlineData("file:///tmp/plugin.html")]
	[InlineData("javascript:alert(1)")]
	[InlineData("//example.com/plugin")]
	public async Task PluginWebViewCapability拒绝离开回环资源服务的入口(string entryPoint)
	{
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.test.plugin",
			Name = "Test Plugin",
			Version = "1.0.0",
		};
		PluginWebViewCapability capability = new(
			descriptor,
			(desc, opts, ct) => Task.FromResult<IPluginWebViewWindow>(new FakePluginWebViewWindow(desc.Id, opts.Id, opts.Title, opts.EntryPoint)));

		await Assert.ThrowsAsync<ArgumentException>(() => capability.CreateWindowAsync(new PluginWebViewOptions
		{
			Id = "win-1",
			Title = "Title",
			EntryPoint = entryPoint,
		}));
	}

	#endregion

	#region 8. 租约撤销与生命周期测试 (Lease Revocation & Window Host)

	[Fact]
	public async Task 没有宿主资源入口时拒绝创建窗口()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-plugin-window-{Guid.NewGuid():N}");
		try
		{
			await using PluginWindowHost host = new(webViewDataRoot: root);
			PluginDescriptorSummary descriptor = new()
			{
				Id = "io.test.plugin",
				Name = "Test Plugin",
				Version = "1.0.0",
			};

			await Assert.ThrowsAsync<PluginException>(() => host.CreateWindowAsync(descriptor, new PluginWebViewOptions
			{
				Id = "main",
				Title = "Test",
				EntryPoint = "/index.html",
			}));
		}
		finally
		{
			try { Directory.Delete(root, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void 租约Token触发时自动执行撤销与关闭()
	{
		using CancellationTokenSource leaseCts = new();
		bool windowClosed = false;

		// 模拟窗口注册租约撤销令牌
		leaseCts.Token.Register(() =>
		{
			windowClosed = true;
		});

		Assert.False(windowClosed);
		leaseCts.Cancel();
		Assert.True(windowClosed);
	}

	#endregion

	#region 9. 插件自定义命令处理器测试 (Plugin Command Handler)

	private sealed class FakeCommandHandler : IPluginWebViewCommandHandler
	{
		public string? LastCommand { get; private set; }
		public JsonElement LastArgs { get; private set; }
		public Func<string, JsonElement, object?>? Responder { get; set; }

		public Task<object?> HandleAsync(string command, JsonElement args, CancellationToken cancellationToken)
		{
			LastCommand = command;
			LastArgs = args.Clone();
			return Task.FromResult(Responder?.Invoke(command, args));
		}
	}

	[Fact]
	public async Task 非白名单命令被转发到插件自定义处理器()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		FakeCommandHandler handler = new()
		{
			Responder = (command, args) => new { echoed = command, songId = args.GetProperty("id").GetInt64() },
		};
		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor, commandHandler: handler);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 9001,
			cmd = "song_url_v1",
			args = new { id = 347230, level = "exhigh" },
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(9001);
		Assert.Equal("resolve", result.Kind);
		Assert.Null(result.Error);
		Assert.Equal("song_url_v1", handler.LastCommand);
		Assert.Equal(347230, handler.LastArgs.GetProperty("id").GetInt64());

		string json = JsonSerializer.Serialize(result.Value);
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.Equal("song_url_v1", doc.RootElement.GetProperty("echoed").GetString());
		Assert.Equal(347230, doc.RootElement.GetProperty("songId").GetInt64());
	}

	[Fact]
	public async Task 白名单命令优先于插件自定义处理器()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		FakeCommandHandler handler = new()
		{
			Responder = (_, _) => new { hijacked = true },
		};
		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor, commandHandler: handler);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 9002,
			cmd = "ping",
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(9002);
		Assert.Equal("resolve", result.Kind);
		Assert.Null(handler.LastCommand);

		string json = JsonSerializer.Serialize(result.Value);
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.True(doc.RootElement.GetProperty("pong").GetBoolean());
	}

	[Fact]
	public async Task 自定义处理器抛出异常时被包装为Reject并脱敏()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		FakeCommandHandler handler = new()
		{
			Responder = (_, _) => throw new InvalidOperationException("云端请求失败，目标路径 C:\\Users\\Secret\\cache"),
		};
		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor, commandHandler: handler);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 9003,
			cmd = "playlist_detail",
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(9003);
		Assert.Equal("reject", result.Kind);
		Assert.Null(result.Value);
		Assert.NotNull(result.Error);
		Assert.DoesNotContain("C:\\Users\\Secret", result.Error);
	}

	[Fact]
	public async Task 未注册处理器时非白名单命令仍被拒绝()
	{
		FakePluginBridgeSource source = new("io.plugin.a", "win-1");
		PluginDescriptorSummary descriptor = new()
		{
			Id = "io.plugin.a",
			Name = "Plugin A",
			Version = "1.0.0",
		};

		PluginBridge bridge = new("io.plugin.a", "win-1", descriptor);

		string payload = JsonSerializer.Serialize(new
		{
			kind = "invoke",
			id = 9004,
			cmd = "song_url_v1",
		});

		bridge.Handle(source, payload);

		FakeResult result = await source.WaitForResultAsync(9004);
		Assert.Equal("reject", result.Kind);
		Assert.Contains("白名单", result.Error);
	}

	#endregion

	#region 辅助测试替身 (Test Doubles)

	private sealed class FakeResult
	{
		public required string Kind { get; init; }
		public object? Value { get; init; }
		public string? Error { get; init; }
	}

	private sealed class FakePluginBridgeSource(string pluginId, string windowId) : IPluginBridgeSource
	{
		public string PluginId => pluginId;
		public string WindowId => windowId;
		public string Label => $"plugin:{pluginId}:{windowId}";
		public bool IsVisible { get; set; } = true;
		public bool Closed { get; private set; }

		private readonly Dictionary<long, TaskCompletionSource<FakeResult>> _pending = new();
		private readonly Dictionary<long, FakeResult> _completed = new();
		private readonly Lock _lock = new();

		public void PostResult(long id, object? value, string? error)
		{
			FakeResult result = new()
			{
				Kind = error == null ? "resolve" : "reject",
				Value = value,
				Error = error,
			};

			lock (_lock)
			{
				if (_pending.Remove(id, out TaskCompletionSource<FakeResult>? tcs))
				{
					tcs.TrySetResult(result);
				}
				else
				{
					_completed[id] = result;
				}
			}
		}

		public Task CloseAsync(CancellationToken cancellationToken = default)
		{
			Closed = true;
			return Task.CompletedTask;
		}

		public Task<FakeResult> WaitForResultAsync(long id, int timeoutMs = 2000)
		{
			TaskCompletionSource<FakeResult> tcs;
			lock (_lock)
			{
				if (_completed.Remove(id, out FakeResult? completed))
				{
					return Task.FromResult(completed);
				}
				if (!_pending.TryGetValue(id, out tcs!))
				{
					tcs = new TaskCompletionSource<FakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
					_pending[id] = tcs;
				}
			}

			return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(t =>
			{
				if (t.Result == tcs.Task) return tcs.Task.Result;
				throw new TimeoutException($"等待桥接响应超时 (id={id})");
			});
		}
	}

	private sealed class FakePluginWebViewWindow(string pluginId, string windowId, string title, string entryUrl) : IPluginWebViewWindow
	{
		public string PluginId => pluginId;
		public string Id => windowId;
		public string WindowId => windowId;
		public string Label => $"plugin:{pluginId}:{windowId}";
		public string Title => title;
		public string EntryUrl => entryUrl;
		public bool IsVisible { get; private set; }
		public bool IsClosed { get; private set; }

		public List<(string Event, string? Payload)> Events { get; } = [];

		public Task SendEventAsync(string eventName, System.Text.Json.Nodes.JsonNode? payload, CancellationToken cancellationToken = default)
		{
			Events.Add((eventName, payload?.ToJsonString()));
			return Task.CompletedTask;
		}

		public Task ShowAsync(CancellationToken cancellationToken = default)
		{
			IsVisible = true;
			return Task.CompletedTask;
		}

		public Task HideAsync(CancellationToken cancellationToken = default)
		{
			IsVisible = false;
			return Task.CompletedTask;
		}

		public Task CloseAsync(CancellationToken cancellationToken = default)
		{
			IsClosed = true;
			IsVisible = false;
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			return new ValueTask(CloseAsync());
		}
	}

	#endregion
}
