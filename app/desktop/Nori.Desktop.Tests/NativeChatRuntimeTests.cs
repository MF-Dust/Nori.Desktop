using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Agent;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class NativeChatHttpHandler : HttpMessageHandler
	{
		public TaskCompletionSource StreamEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource SpeechEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string StreamBody { get; set; } = "event: delta\ndata: {\"text\":\"临时投影\"}\n\nevent: done\ndata: {\"text\":\"服务端最终文本\"}\n\n";
		public int ResetCount;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string path = request.RequestUri!.AbsolutePath;
			if (path.EndsWith("/messages/stream", StringComparison.Ordinal))
			{
				StreamEntered.TrySetResult();
				await ReleaseStream.Task.WaitAsync(cancellationToken);
				return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(StreamBody, Encoding.UTF8, "text/event-stream")};
			}
			if (path.EndsWith("/reset", StringComparison.Ordinal))
			{
				Interlocked.Increment(ref ResetCount);
				return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"ok\":true,\"commitId\":\"reset\"}", Encoding.UTF8, "application/json")};
			}
			if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
			{
				SpeechEntered.TrySetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			throw new InvalidOperationException("测试遇到意外的外部请求");
		}
	}

	private AppRuntime CreateNativeChatRuntime(HttpClient http)
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(true));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("https://chat.invalid"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("native-chat-secret"));
		_config.Set(LuoLiCoreSettingsStore.KeySessionId, new ConfigValue.Text("session-native"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text(""));
		_services.Http = http;
		AppRuntime runtime = new(_services);
		_services.Runtime = runtime;
		return runtime;
	}

	[Fact]
	public Task NativeChatClearCannotRaceAcceptedGenerationAndCompleteReleasesBeforeSpeech() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set("tts_auto_play", new ConfigValue.Boolean(true));
		_config.Set("tts_provider", new ConfigValue.Text("openai"));
		_config.Set("tts_base_url", new ConfigValue.Text("https://chat.invalid/v1"));
		_config.Set("tts_api_key", new ConfigValue.Text("test-voice-secret"));
		using NativeChatTestSource source = new();
		TaskCompletionSource<JsonElement> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() != "complete") return;
			try
			{
				Assert.False(runtime.IsSessionActive(payload.GetProperty("sessionId").GetString()!));
				using AgentSessionLease probe = runtime.Engine.ReserveSession("terminal-probe");
				completed.TrySetResult(payload);
			}
			catch (Exception exception) { completed.TrySetException(exception); }
		};
		string sessionId = runtime.StartChat(source, "本次输入");
		Assert.True(runtime.IsSessionActive(sessionId));
		Assert.Throws<AgentSessionBusyException>(() => runtime.StartChat(source, "重复发送"));
		await Assert.ThrowsAsync<AgentSessionBusyException>(() => CreateCommands().InvokeAsync(source, "chat_clear", Args(new { })));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Equal(0, handler.ResetCount);
		handler.ReleaseStream.TrySetResult();
		JsonElement terminal = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Equal("服务端最终文本", terminal.GetProperty("message").GetProperty("text").GetString());
		Assert.Equal("服务端最终文本", _services.Chat.GetHistory()[^1].Content);
		await handler.SpeechEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		await CreateCommands().InvokeAsync(source, "chat_clear", Args(new { })).WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Equal(1, handler.ResetCount);
		Assert.Empty(_services.Chat.GetHistory());
		Assert.DoesNotContain(source.Events, item => item.GetProperty("type").GetString() == "state" && !item.TryGetProperty("sessionId", out _));
		Assert.DoesNotContain(_windows.Broadcasts, item => item.Name == AppRuntime.AgentEventName);
	});

	[Fact]
	public Task NativeChatCancelAndEventsStayBoundToOriginalContext() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using NativeChatTestSource original = new();
		using NativeChatTestSource reopened = new();
		TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		original.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "cancelled") cancelled.TrySetResult();
		};
		string sessionId = runtime.StartChat(original, "可取消的输入");
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.False(runtime.CancelChat(reopened, sessionId));
		Assert.False(runtime.CancelChat(new FakeBridgeSource(WindowLabels.Main), sessionId));
		Assert.False(runtime.CancelChat(new FakeBridgeSource(WindowLabels.Chat), sessionId));
		Assert.True(runtime.CancelChat(original, sessionId));
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Empty(reopened.Events);
		Assert.Empty(_services.Chat.GetHistory());
		using AgentSessionLease lease = runtime.Engine.ReserveSession("after-cancel");
	});

	[Fact]
	public Task NativeChatSourceDisposalCancelsWithoutDeliveringIntoReopenedWindow() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using NativeChatTestSource original = new();
		using NativeChatTestSource reopened = new();
		string sessionId = runtime.StartChat(original, "关闭来源");
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		original.Dispose();
		await WaitUntilAsync(() => !runtime.IsSessionActive(sessionId));
		Assert.Empty(reopened.Events);
		Assert.Empty(_services.Chat.GetHistory());
	});

	[Fact]
	public Task NativeChatFailureIsRedactedAndReleasesEngineLease() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new()
		{
			StreamBody = "event: error\ndata: {\"code\":\"failure\",\"message\":\"token=very-private /home/user/private.txt\",\"retryable\":false}\n\n",
		};
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using NativeChatTestSource source = new();
		TaskCompletionSource<JsonElement> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload => { if (payload.GetProperty("type").GetString() == "error") failed.TrySetResult(payload); };
		runtime.StartChat(source, "失败测试");
		handler.ReleaseStream.TrySetResult();
		JsonElement terminal = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.DoesNotContain("very-private", terminal.GetRawText(), StringComparison.Ordinal);
		Assert.DoesNotContain("private.txt", terminal.GetRawText(), StringComparison.Ordinal);
		Assert.Empty(_services.Chat.GetHistory());
		using AgentSessionLease lease = runtime.Engine.ReserveSession("after-error");
	});

	[Fact]
	public async Task NativeChatClearAlsoHonorsDirectEngineLeaseAndCommittedCancellation()
	{
		using NativeChatTestSource source = new();
		_services.Chat.SaveMessage("user", "不能丢失");
		using (AgentSessionLease directEngineSession = _runtime.Engine.ReserveSession("direct-engine"))
			await Assert.ThrowsAsync<AgentSessionBusyException>(() => CreateCommands().InvokeAsync(source, "chat_clear", Args(new { })));
		Assert.Single(_services.Chat.GetHistory());
		using CancellationTokenSource cancellation = new();
		void CancelCommitted() => cancellation.Cancel();
		_runtime.StateChanged += CancelCommitted;
		try
		{
			await CreateCommands().InvokeAsync(source, "chat_clear", Args(new { }), cancellation.Token);
			Assert.True(cancellation.IsCancellationRequested);
			Assert.Empty(_services.Chat.GetHistory());
		}
		finally { _runtime.StateChanged -= CancelCommitted; }
	}

	[Fact]
	public async Task NativeChatApprovalExtensionIsSourceScopedAndCappedToToolDeadline()
	{
		using NativeChatTestSource source = new();
		using NativeChatTestSource reopened = new();
		DateTimeOffset maximum = DateTimeOffset.UtcNow.AddSeconds(90);
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = "extend-one", ToolName = "tool", PermissionLevel = "confirm", DeadlineUtc = maximum,
		}, CancellationToken.None);
		JsonElement request = Assert.Single(source.Events);
		Assert.True(request.GetProperty("deadlineUtc").GetDateTimeOffset() < maximum);
		Assert.False(_runtime.RespondApproval(reopened, "extend-one", true));
		Assert.False(_runtime.RespondApproval(new FakeBridgeSource(WindowLabels.Chat), "extend-one", true));
		Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(reopened, "extend-one"));
		JsonElement result = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(source, "approval_extend", Args(new {requestId = "extend-one"})), BridgeJson.Options);
		Assert.Equal(maximum, result.GetProperty("deadlineUtc").GetDateTimeOffset());
		Assert.Equal(maximum, _runtime.ExtendApproval(source, "extend-one"));
		Assert.Contains(source.Events, item => item.GetProperty("type").GetString() == "approval-extended" && item.GetProperty("deadlineUtc").GetDateTimeOffset() == maximum);
		Assert.True(_runtime.RespondApproval(source, "extend-one", false));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(source, "extend-one"));
		Assert.Empty(reopened.Events);
	}

	[Fact]
	public async Task NativeChatHiddenNativeRuntimeCannotApproveOrExtendButCanDeny()
	{
		using NativeChatTestSource source = new();
		source.IsVisible = false;
		string requestId = "hidden-runtime";
		TaskCompletionSource requestPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "approval-request"
				&& payload.GetProperty("requestId").GetString() == requestId)
			{
				requestPosted.TrySetResult();
			}
		};
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = requestId, ToolName = "tool", PermissionLevel = "confirm",
		}, CancellationToken.None);
		await requestPosted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Throws<InvalidOperationException>(() => _runtime.RespondApproval(source, requestId, true));
		Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(source, requestId));
		Assert.True(_runtime.RespondApproval(source, requestId, false));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(source, requestId));
	}

	[Fact]
	public async Task NativeChatApprovalCommandCannotApproveOrExtendAfterVisibilityLostWhileWaitingApprovalGate()
	{
		using NativeChatTestSource source = new();
		BridgeCommands commands = CreateCommands();
		string approveRequest = "queue-hidden-approve";
		string extendRequest = "queue-hidden-extend";
		TaskCompletionSource approvePosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource extendPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() != "approval-request") return;
			string requestId = payload.GetProperty("requestId").GetString()!;
			if (requestId == approveRequest) approvePosted.TrySetResult();
			else if (requestId == extendRequest) extendPosted.TrySetResult();
		};
		Task<bool> approveDecision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = approveRequest, ToolName = "tool", PermissionLevel = "confirm",
		}, CancellationToken.None);
		Task<bool> extendDecision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = extendRequest, ToolName = "tool", PermissionLevel = "confirm",
		}, CancellationToken.None);
		await Task.WhenAll(
			approvePosted.Task.WaitAsync(TimeSpan.FromSeconds(2)),
			extendPosted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		foreach ((string command, string requestId, int entryChecks) in new[]
		{
			("approval_respond", approveRequest, 2), ("approval_extend", extendRequest, 1),
		})
		{
			using ManualResetEventSlim validated = new();
			int reads = 0;
			source.IsVisible = true;
			source.VisibilityRead = () => { if (Interlocked.Increment(ref reads) == entryChecks) validated.Set(); };
			Task<object?> queued;
			lock (GetApprovalGate(_runtime))
			{
				queued = Task.Run(() => commands.InvokeAsync(source, command, Args(new { requestId, approved = true })));
				// 入口已读到可见=true，实际提交仍阻塞在当前线程持有的锁上。
				Assert.True(validated.Wait(TimeSpan.FromSeconds(5)));
				source.IsVisible = false;
			}
			await Assert.ThrowsAsync<InvalidOperationException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
			source.VisibilityRead = null;
			Assert.True(_runtime.RespondApproval(source, requestId, false));
		}
		Assert.False(await approveDecision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.False(await extendDecision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public async Task NativeChatCancellationSignalPendingOnApprovalGateCannotApproveOrExtend()
	{
		using NativeChatTestSource source = new();
		string approveRequest = "cancel-pending-approve";
		using CancellationTokenSource approvalCancellation = new();
		TaskCompletionSource approvePosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "approval-request"
				&& payload.GetProperty("requestId").GetString() == approveRequest)
			{
				approvePosted.TrySetResult();
			}
		};
		Task<bool> approveDecision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = approveRequest, ToolName = "tool", PermissionLevel = "confirm",
			CancellationToken = approvalCancellation.Token,
		}, CancellationToken.None);
		await approvePosted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Task approvalCancelled;
		CancellationToken approvalSignal = GetPendingApprovalToken(_runtime, approveRequest);
		lock (GetApprovalGate(_runtime))
		{
			approvalCancelled = approvalCancellation.CancelAsync();
			Assert.True(SpinWait.SpinUntil(() => approvalSignal.IsCancellationRequested, TimeSpan.FromSeconds(5)));
			// 撤销回调尚无法取得锁，当前线程重入以验证提交本身也检查取消信号。
			Assert.False(_runtime.RespondApproval(source, approveRequest, true));
		}
		await approvalCancelled.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(await approveDecision.WaitAsync(TimeSpan.FromSeconds(2)));

		string extendRequest = "cancel-pending-extend";
		using CancellationTokenSource extendCancellation = new();
		TaskCompletionSource extendPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "approval-request"
				&& payload.GetProperty("requestId").GetString() == extendRequest)
			{
				extendPosted.TrySetResult();
			}
		};
		Task<bool> extendDecision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = extendRequest, ToolName = "tool", PermissionLevel = "confirm",
			CancellationToken = extendCancellation.Token,
		}, CancellationToken.None);
		await extendPosted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Task extensionCancelled;
		CancellationToken extensionSignal = GetPendingApprovalToken(_runtime, extendRequest);
		lock (GetApprovalGate(_runtime))
		{
			extensionCancelled = extendCancellation.CancelAsync();
			Assert.True(SpinWait.SpinUntil(() => extensionSignal.IsCancellationRequested, TimeSpan.FromSeconds(5)));
			Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(source, extendRequest));
		}
		await extensionCancelled.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(await extendDecision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public async Task NativeChatToolTimeoutWithdrawsApprovalAndNeverExecutes()
	{
		using NativeChatTestSource source = new();
		int executed = 0;
		ToolRegistry tools = new();
		tools.Register(new RegisteredTool
		{
			Name = "deadline-tool", Description = "测试授权", Parameters = new JsonObject(), PermissionLevel = "confirm",
			Execute = (_, _) => { Interlocked.Increment(ref executed); return Task.FromResult<object?>(null); },
		});
		ToolResult result = await tools.ExecuteAsync("deadline-tool", null, new ToolContext
		{
			DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(200),
			Approve = request => _runtime.RequestApprovalAsync(source, "tool-session", request, CancellationToken.None),
		});
		Assert.False(result.IsSuccess);
		Assert.Equal(0, executed);
		JsonElement request = source.Events.First(item => item.GetProperty("type").GetString() == "approval-request");
		string requestId = request.GetProperty("requestId").GetString()!;
		await WaitUntilAsync(() => source.Events.Any(item => item.GetProperty("type").GetString() == "approval-result"));
		Assert.False(_runtime.RespondApproval(source, requestId, true));
		Assert.Throws<InvalidOperationException>(() => _runtime.ExtendApproval(source, requestId));
	}

	private static CancellationToken GetPendingApprovalToken(AppRuntime runtime, string requestId)
	{
		object? pending = ((System.Collections.IDictionary)typeof(AppRuntime)
			.GetField("_approvals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!)[requestId];
		Assert.NotNull(pending);
		return Assert.IsType<CancellationToken>(pending.GetType().GetProperty("CancellationToken")!.GetValue(pending));
	}

	private static System.Threading.Lock GetApprovalGate(AppRuntime runtime)
	{
		FieldInfo field = typeof(AppRuntime).GetField("_approvalGate", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("未找到 approval gate 字段");
		return Assert.IsType<System.Threading.Lock>(field.GetValue(runtime));
	}
}
