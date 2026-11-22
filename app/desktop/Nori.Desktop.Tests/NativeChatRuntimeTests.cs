using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Nori.Desktop.Chat;
using Nori.Core.Agent;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Tools;
using Nori.Core.Voice.Realtime;
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
	public Task RealtimeVoiceFirstTurnWaitsForSupersedingRefreshAndHistory() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set("tts_auto_play", new ConfigValue.Boolean(false));
		Window owner = new();
		using NativeChatService service = new(_services, owner);
		TaskCompletionSource<JsonElement> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<JsonElement> newest = new(TaskCreationOptions.RunContinuationsAsynchronously);
		JsonElement snapshot = JsonSerializer.SerializeToElement(new
		{
			chat = new { configured = true },
			app = new { safeMode = false },
			general = new { language = "zh-CN" },
		});
		int snapshotCalls = 0;
		int historyCalls = 0;
		Task<JsonElement> GetSnapshot(CancellationToken _) => Interlocked.Increment(ref snapshotCalls) switch
		{
			1 => first.Task,
			2 => newest.Task,
			_ => Task.FromResult(snapshot),
		};
		Task<JsonElement> Execute(string command, object? _, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (command != "chat_history_page") throw new InvalidOperationException("意外调用");
			Interlocked.Increment(ref historyCalls);
			return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
		}
		using ChatView view = new(service, Execute, GetSnapshot);
		owner.Content = view;
		owner.Show();

		Task voice = view.SendRealtimeTurnAsync(new RealtimeVoiceTurn(81, "第一轮语音", CancellationToken.None));
		Assert.Equal(1, snapshotCalls);
		Task superseding = view.RefreshAsync();
		Assert.Equal(2, snapshotCalls);
		first.TrySetResult(snapshot);
		await Task.Yield();
		Assert.False(voice.IsCompleted);
		Assert.Equal(0, historyCalls);

		newest.TrySetResult(snapshot);
		await superseding.WaitAsync(TimeSpan.FromSeconds(3));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		handler.ReleaseStream.TrySetResult();
		await voice.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.True(view.HistoryLoaded);
		Assert.Equal(1, historyCalls);
		Assert.Contains(_services.Chat.GetHistory(), message => message.Content == "第一轮语音");
		owner.Close();
	});

	[Fact]
	public void RealtimeVoiceAgentRejectsUntrustedSource()
	{
		RealtimeVoiceTurn turn = new(1, "测试", CancellationToken.None);
		Assert.Throws<InvalidOperationException>(() => _runtime.StartRealtimeChat(new FakeBridgeSource(WindowLabels.Main), turn));
		Assert.Throws<InvalidOperationException>(() => _runtime.StartRealtimeChat(new FakeBridgeSource(WindowLabels.Chat), turn));
		using NativeChatTestSource mislabelled = new(WindowLabels.Main);
		Assert.Throws<InvalidOperationException>(() => _runtime.StartRealtimeChat(mislabelled, turn));
	}

	[Theory]
	[InlineData("trusted")]
	[InlineData("bypass")]
	[InlineData("session")]
	public Task RealtimeVoiceToolApprovalAlwaysRequiresFreshConsent(string gear) => WithRealtimeChatSourceAsync(async (source, events) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text(gear));
		if (gear == "bypass")
			_config.Set(ToolPermissionPolicy.KeyBypassUntil,
				new ConfigValue.Text(ToolPermissionPolicy.FormatDeadline(DateTimeOffset.UtcNow.AddMinutes(10))));
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "语音请求", CancellationToken.None));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		for (int index = 0; index < 2; index++)
		{
			ToolApprovalRequest approval = new()
			{
				RequestId = $"voice-approval-{gear}-{index}",
				ToolName = "writeFile",
				PermissionLevel = "confirm",
			};
			Task<bool> pending = runtime.RequestApprovalAsync(source, started.SessionId, approval, CancellationToken.None);
			Assert.False(pending.IsCompleted);
			Assert.Contains(events, item =>
				item.GetProperty("type").GetString() == "approval-request"
				&& item.GetProperty("requestId").GetString() == approval.RequestId);
			Assert.True(runtime.RespondApproval(source, approval.RequestId, true));
			Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.False(runtime.Permissions.Remembered(started.SessionId, "writeFile"));
		}
		handler.ReleaseStream.TrySetResult();
		await started.Completion.WaitAsync(TimeSpan.FromSeconds(3));
	});

	[Fact]
	public Task RealtimeVoiceTurnUsesExistingNativeAgentAndCompletesBeforeReturning() => WithRealtimeChatSourceAsync(async (source, events) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set("tts_auto_play", new ConfigValue.Boolean(false));
		RealtimeVoiceTurn turn = new(7, "语音提问", CancellationToken.None);
		var started = runtime.StartRealtimeChat(source, turn);
		Assert.True(runtime.IsSessionActive(started.SessionId));
		Assert.Throws<InvalidOperationException>(() => runtime.StartRealtimeChat(source, turn));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		handler.ReleaseStream.TrySetResult();
		await started.Completion.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.False(runtime.IsSessionActive(started.SessionId));
		Assert.Throws<InvalidOperationException>(() => runtime.StartRealtimeChat(source, turn));
		Assert.Contains(events, item => item.GetProperty("type").GetString() == "complete"
			&& item.GetProperty("sessionId").GetString() == started.SessionId);
		Assert.Equal("服务端最终文本", _services.Chat.GetHistory()[^1].Content);
	});

	[Fact]
	public Task RealtimeVoiceAgentRejectsCancelledDeliveredAndTextTurns() => WithRealtimeChatSourceAsync((source, _) =>
	{
		RealtimeVoiceTurn cancelled = new(1, "已取消", new CancellationToken(true));
		RealtimeVoiceTurn delivered = new(1, "已交付", CancellationToken.None);
		RealtimeVoiceTurn text = new(1, "普通文本", CancellationToken.None, origin: VoiceTurnOrigin.Text);
		Assert.True(delivered.TryMarkFinalDelivered());
		Assert.ThrowsAny<OperationCanceledException>(() => _runtime.StartRealtimeChat(source, cancelled));
		Assert.Throws<InvalidOperationException>(() => _runtime.StartRealtimeChat(source, delivered));
		Assert.Throws<InvalidOperationException>(() => _runtime.StartRealtimeChat(source, text));
		using AgentSessionLease lease = _runtime.Engine.ReserveSession("no-rejected-voice-turn");
		return Task.CompletedTask;
	});

	[Fact]
	public Task RealtimeVoiceInterruptCancelsLinkedAgentWithoutReplayingTurn() => WithRealtimeChatSourceAsync(async (source, events) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set("tts_auto_play", new ConfigValue.Boolean(false));
		using CancellationTokenSource interrupt = new();
		RealtimeVoiceTurn turn = new(8, "可被打断的输入", interrupt.Token);
		var started = runtime.StartRealtimeChat(source, turn);
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		ToolApprovalRequest approval = new()
		{
			RequestId = "interrupted-voice-approval",
			ToolName = "writeFile",
			PermissionLevel = "confirm",
		};
		Task<bool> pending = runtime.RequestApprovalAsync(source, started.SessionId, approval, CancellationToken.None);
		Assert.False(pending.IsCompleted);
		Assert.Throws<InvalidOperationException>(() => runtime.StartRealtimeChat(source, turn));
		interrupt.Cancel();
		Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.False(runtime.RespondApproval(source, approval.RequestId, true));
		await started.Completion.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.False(runtime.IsSessionActive(started.SessionId));
		Assert.DoesNotContain(events, item => item.GetProperty("type").GetString() == "complete");
		Assert.Empty(_services.Chat.GetHistory());
		Assert.ThrowsAny<OperationCanceledException>(() => runtime.StartRealtimeChat(source, turn));
		using AgentSessionLease lease = runtime.Engine.ReserveSession("after-realtime-cancel");
	});

	private static Task WithRealtimeChatSourceAsync(Func<NativeChatContext, ConcurrentQueue<JsonElement>, Task> test) =>
		WithSettingsUiAsync(async () =>
		{
			Window owner = new();
			try
			{
				owner.Show();
				using NativeChatContext source = new(owner);
				ConcurrentQueue<JsonElement> events = new();
				source.EventReceived += (_, payload) => events.Enqueue(JsonSerializer.SerializeToElement(payload, BridgeJson.Options));
				await test(source, events);
			}
			finally { owner.Close(); }
		});

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
