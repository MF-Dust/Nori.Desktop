using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Voice;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

/// <summary>OpenAI Realtime 转写协议、URL 校验与模拟传输测试。</summary>
public sealed class OpenAiRealtimeSttTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-rt-stt-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public OpenAiRealtimeSttTests()
	{
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	[Theory]
	[InlineData(null, "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com/v1", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com/v1/", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com/v1/v1/", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com/v1/audio/transcriptions", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://api.openai.com/v1/realtime?intent=transcription", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("https://proxy.example.com/openai/v1/v1/", "wss://proxy.example.com/openai/v1/realtime?intent=transcription")]
	[InlineData("wss://api.openai.com/v1", "wss://api.openai.com/v1/realtime?intent=transcription")]
	[InlineData("http://127.0.0.1:8080/v1", "ws://127.0.0.1:8080/v1/realtime?intent=transcription")]
	[InlineData("ws://127.0.0.1:8080/v1/", "ws://127.0.0.1:8080/v1/realtime?intent=transcription")]
	[InlineData("http://localhost:9000", "ws://localhost:9000/v1/realtime?intent=transcription")]
	[InlineData("ws://[::1]:8080/v1", "ws://[::1]:8080/v1/realtime?intent=transcription")]
	public void URL构造支持规范化与本地环回(string? input, string expected)
	{
		Uri uri = OpenAiRealtimeSttUriBuilder.Build(input);
		Assert.Equal(expected, uri.ToString());
	}

	[Theory]
	[InlineData("http://example.com/v1")]
	[InlineData("ws://example.com/v1")]
	[InlineData("http://192.168.1.20:8080/v1")]
	[InlineData("ws://10.0.0.5/v1")]
	[InlineData("ftp://api.openai.com/v1")]
	[InlineData("not-a-uri")]
	public void 拒绝非环回明文与非法协议地址(string badUrl)
	{
		Assert.Throws<InvalidOperationException>(() => OpenAiRealtimeSttUriBuilder.Build(badUrl));
	}

	[Theory]
	[InlineData("https://sk-leak-secret-123@api.openai.com/v1", "sk-leak-secret-123")]
	[InlineData("https://api.openai.com/v1?api_key=sk-query-secret-456", "sk-query-secret-456")]
	[InlineData("https://api.openai.com/v1?token=tok-secret-789", "tok-secret-789")]
	[InlineData("https://api.openai.com/v1?foo=arbitrary-secret-000", "arbitrary-secret-000")]
	[InlineData("https://api.openai.com/v1?intent=other", "other")]
	public void 拒绝URL内嵌凭据与非Intent查询参数且异常不含密钥(string badUrl, string secret)
	{
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => OpenAiRealtimeSttUriBuilder.Build(badUrl));
		Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 建连鉴权头无Beta头且SessionUpdate字段精确匹配GA规范()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			BaseUrl = "https://api.openai.com/v1/",
			ApiKey = "sk-live-secret-test",
			Language = "zh",
			HeartbeatInterval = TimeSpan.FromSeconds(15),
		});

		Assert.True(session.IsConnected);
		Assert.Equal("openai_realtime", session.ProviderId);
		Assert.Equal(new Uri("wss://api.openai.com/v1/realtime?intent=transcription"), transport.ConnectedUri);
		Assert.Equal(TimeSpan.FromSeconds(15), transport.KeepAliveInterval);

		Assert.True(transport.Headers.TryGetValue("Authorization", out string? auth));
		Assert.Equal("Bearer sk-live-secret-test", auth);
		Assert.False(transport.Headers.ContainsKey("OpenAI-Beta"));
		Assert.DoesNotContain("sk-live-secret-test", transport.ConnectedUri!.ToString(), StringComparison.Ordinal);

		string sentJson = Assert.Single(transport.SentTextMessages);
		JsonObject root = JsonNode.Parse(sentJson)!.AsObject();
		Assert.Equal("session.update", root["type"]?.GetValue<string>());

		JsonObject sessionNode = root["session"]!.AsObject();
		Assert.Equal("transcription", sessionNode["type"]?.GetValue<string>());

		JsonObject inputNode = sessionNode["audio"]!["input"]!.AsObject();
		Assert.Equal("audio/pcm", inputNode["format"]!["type"]?.GetValue<string>());
		Assert.Equal(24000, inputNode["format"]!["rate"]?.GetValue<int>());
		Assert.True(inputNode.ContainsKey("turn_detection"));
		Assert.Null(inputNode["turn_detection"]);

		JsonObject transcriptionNode = inputNode["transcription"]!.AsObject();
		Assert.Equal("gpt-live-transcribe", transcriptionNode["model"]?.GetValue<string>());
		Assert.Equal("zh", transcriptionNode["language"]?.GetValue<string>());

		StreamingSttEvent readyEvent = await session.Events.ReadAsync();
		Assert.Equal(StreamingSttEventKind.SessionUpdated, readyEvent.Kind);
		Assert.Equal(1, readyEvent.Sequence);
	}

	[Fact]
	public async Task SessionUpdate支持省略Language并覆盖模型名()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			BaseUrl = "http://127.0.0.1:8080/v1",
			ApiKey = "sk-test",
			Model = "custom-realtime-model",
			Language = "   ",
		});

		JsonObject root = JsonNode.Parse(Assert.Single(transport.SentTextMessages))!.AsObject();
		JsonObject transcription = root["session"]!["audio"]!["input"]!["transcription"]!.AsObject();
		Assert.Equal("custom-realtime-model", transcription["model"]?.GetValue<string>());
		Assert.False(transcription.ContainsKey("language"));
	}

	[Fact]
	public async Task 建连异常发送异常与服务端Error对ApiKey全链严格脱敏()
	{
		const string secret = "my-super-secret-key-999";

		FakeRealtimeTransport failingConnectTransport = new()
		{
			ConnectException = new WebSocketException(
				$"Handshake failed for Bearer {secret} at wss://api.openai.com",
				new InvalidOperationException($"inner {secret}")),
		};
		OpenAiRealtimeSttProvider connectProvider = new(_config, () => failingConnectTransport);

		VoiceProviderException connectEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			connectProvider.OpenStreamingSessionAsync(new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.Network, connectEx.FailureKind);
		Assert.Null(connectEx.InnerException);
		Assert.DoesNotContain(secret, connectEx.ToString(), StringComparison.Ordinal);

		FakeRealtimeTransport serverErrorTransport = new();
		serverErrorTransport.QueueIncomingText(
			JsonSerializer.Serialize(new
			{
				type = "error",
				error = new
				{
					type = "invalid_request_error",
					code = "invalid_api_key",
					message = $"Incorrect API key provided: {secret}",
				},
			}));
		OpenAiRealtimeSttProvider errorProvider = new(_config, () => serverErrorTransport);

		VoiceProviderException readyEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			errorProvider.OpenStreamingSessionAsync(new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.ProviderRejected, readyEx.FailureKind);
		Assert.Null(readyEx.InnerException);
		Assert.DoesNotContain(secret, readyEx.ToString(), StringComparison.Ordinal);
		Assert.True(serverErrorTransport.Disposed);

		// 运行期发送失败同样不得在 Exception.ToString() 中留下密钥
		FakeRealtimeTransport sendFailTransport = new();
		sendFailTransport.QueueIncomingText("{\"type\":\"session.updated\"}");
		OpenAiRealtimeSttProvider sendFailProvider = new(_config, () => sendFailTransport);
		await using IStreamingSttSession session = await sendFailProvider.OpenStreamingSessionAsync(new SttRequestOptions { ApiKey = secret });
		sendFailTransport.SendException = new WebSocketException($"send failed with {secret}", new IOException(secret));

		VoiceProviderException sendEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			session.AppendAudioAsync(new byte[] { 1, 2, 3, 4 }));
		Assert.Equal(VoiceFailureKind.Network, sendEx.FailureKind);
		Assert.Null(sendEx.InnerException);
		Assert.DoesNotContain(secret, sendEx.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 就绪前关闭或超时会失败并清理传输()
	{
		FakeRealtimeTransport closedTransport = new();
		closedTransport.QueueClose();
		OpenAiRealtimeSttProvider closedProvider = new(_config, () => closedTransport);

		VoiceProviderException closeEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			closedProvider.OpenStreamingSessionAsync(new SttRequestOptions { ApiKey = "sk-1" }));
		Assert.Equal(VoiceFailureKind.Network, closeEx.FailureKind);
		Assert.True(closedTransport.Disposed);

		FakeRealtimeTransport silentTransport = new();
		OpenAiRealtimeSttProvider timeoutProvider = new(_config, () => silentTransport);

		VoiceProviderException timeoutEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			timeoutProvider.OpenStreamingSessionAsync(new SttRequestOptions
			{
				ApiKey = "sk-1",
				SessionReadyTimeout = TimeSpan.FromMilliseconds(40),
			}));
		Assert.Equal(VoiceFailureKind.Timeout, timeoutEx.FailureKind);
		Assert.True(silentTransport.Disposed);
	}

	[Fact]
	public async Task AppendAudio与Commit编码正确且并发发送串行安全()
	{
		FakeRealtimeTransport transport = new()
		{
			SendDelay = TimeSpan.FromMilliseconds(5),
		};
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
		});

		await Assert.ThrowsAsync<ArgumentException>(() => session.AppendAudioAsync(new byte[] { 1, 2, 3 }));

		await session.AppendAudioAsync(ReadOnlyMemory<byte>.Empty);
		Assert.Single(transport.SentTextMessages); // 仅 session.update

		byte[] pcmA = [0x01, 0x02, 0x03, 0x04];
		byte[] pcmB = [0x10, 0x20, 0x30, 0x40];

		Task[] sends =
		[
			session.AppendAudioAsync(pcmA),
			session.AppendAudioAsync(pcmB),
			session.AppendAudioAsync(pcmA),
			session.CommitAsync(),
		];
		await Task.WhenAll(sends);

		Assert.Equal(1, transport.MaxConcurrentSends);
		Assert.Equal(5, transport.SentTextMessages.Count);

		List<string> appendedBase64 = [];
		bool sawCommit = false;
		foreach (string raw in transport.SentTextMessages.Skip(1))
		{
			JsonObject msg = JsonNode.Parse(raw)!.AsObject();
			string type = msg["type"]!.GetValue<string>();
			if (type == "input_audio_buffer.append")
			{
				appendedBase64.Add(msg["audio"]!.GetValue<string>());
			}
			else if (type == "input_audio_buffer.commit")
			{
				sawCommit = true;
			}
		}

		Assert.True(sawCommit);
		Assert.Equal(3, appendedBase64.Count);
		Assert.Contains(Convert.ToBase64String(pcmA), appendedBase64);
		Assert.Contains(Convert.ToBase64String(pcmB), appendedBase64);
	}

	[Fact]
	public async Task 分片消息重组包含跨帧UTF8字符并拦截超长帧()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		// 把含多字节 UTF-8 中文字符的 JSON 在字节中间切分成 3 片
		byte[] fullBytes = Encoding.UTF8.GetBytes(
			"{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_utf8\",\"content_index\":0,\"delta\":\"你好世界\"}");
		int split1 = fullBytes.Length - 11; // 故意落在 UTF-8 中文多字节内部
		int split2 = fullBytes.Length - 5;
		transport.QueueIncomingFragment(fullBytes[..split1], endOfMessage: false);
		transport.QueueIncomingFragment(fullBytes[split1..split2], endOfMessage: false);
		transport.QueueIncomingFragment(fullBytes[split2..], endOfMessage: true);

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
		});

		_ = await session.Events.ReadAsync(); // SessionUpdated
		StreamingSttEvent delta = await session.Events.ReadAsync();
		Assert.Equal(StreamingSttEventKind.Delta, delta.Kind);
		Assert.Equal("item_utf8", delta.ItemId);
		Assert.Equal("你好世界", delta.Text);

		// 注入超过 MaxIncomingMessageBytes 的恶意分片帧
		byte[] hugeChunk = new byte[OpenAiRealtimeSttSession.MaxIncomingMessageBytes / 2 + 128];
		Array.Fill(hugeChunk, (byte)'a');
		transport.QueueIncomingFragment(hugeChunk, endOfMessage: false);
		transport.QueueIncomingFragment(hugeChunk, endOfMessage: true);

		StreamingSttEvent errorEvt = await session.Events.ReadAsync();
		Assert.Equal(StreamingSttEventKind.Error, errorEvt.Kind);
		Assert.Equal("message_too_large", errorEvt.ErrorCode);

		StreamingSttEvent closedEvt = await session.Events.ReadAsync();
		Assert.Equal(StreamingSttEventKind.Closed, closedEvt.Kind);
		Assert.True(transport.Aborted);
		Assert.False(session.IsConnected);
	}

	[Fact]
	public async Task 解析服务端全量事件并保持乱序与多ItemId关联和单调Sequence()
	{
		const string secret = "sk-item-secret";
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");
		// delta for item_B arrives BEFORE committed for item_B, and interleaved with item_A
		transport.QueueIncomingText("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"item_A\"}");
		transport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_B\",\"content_index\":0,\"delta\":\"先到\"}");
		transport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_A\",\"content_index\":0,\"delta\":\"你\"}");
		transport.QueueIncomingText("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"item_B\"}");
		transport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"item_A\",\"content_index\":0,\"transcript\":\"你好\"}");
		transport.QueueIncomingText(
			JsonSerializer.Serialize(new
			{
				type = "conversation.item.input_audio_transcription.failed",
				item_id = "item_B",
				content_index = 1,
				error = new
				{
					type = "server_error",
					code = "tx_fail",
					message = $"failed with {secret}",
				},
			}));
		transport.QueueIncomingText(
			JsonSerializer.Serialize(new
			{
				type = "error",
				error = new
				{
					type = "rate_limit_error",
					code = "rate_limit",
					message = $"slow down {secret}",
				},
			}));
		transport.QueueClose();

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = secret,
		});

		List<StreamingSttEvent> events = [];
		await foreach (StreamingSttEvent item in session.Events.ReadAllAsync())
		{
			events.Add(item);
		}

		Assert.Equal(9, events.Count);
		Assert.Equal(
			[
				StreamingSttEventKind.SessionUpdated,
				StreamingSttEventKind.InputCommitted,
				StreamingSttEventKind.Delta,
				StreamingSttEventKind.Delta,
				StreamingSttEventKind.InputCommitted,
				StreamingSttEventKind.Completed,
				StreamingSttEventKind.Failed,
				StreamingSttEventKind.Error,
				StreamingSttEventKind.Closed,
			],
			events.Select(e => e.Kind).ToArray());

		// Sequence 严格单调递增
		for (int i = 0; i < events.Count; i++)
		{
			Assert.Equal(i + 1, events[i].Sequence);
		}

		Assert.Equal("item_A", events[1].ItemId);
		Assert.Equal("item_B", events[2].ItemId);
		Assert.Equal("先到", events[2].Text);
		Assert.Equal("item_A", events[3].ItemId);
		Assert.Equal("你", events[3].Text);
		Assert.Equal("item_B", events[4].ItemId);
		Assert.Equal("item_A", events[5].ItemId);
		Assert.Equal(0, events[5].ContentIndex);
		Assert.Equal("你好", events[5].Text);

		Assert.Equal("item_B", events[6].ItemId);
		Assert.Equal(1, events[6].ContentIndex);
		Assert.Equal("tx_fail", events[6].ErrorCode);
		Assert.Equal("server_error", events[6].ErrorType);
		Assert.DoesNotContain(secret, events[6].ErrorMessage ?? "", StringComparison.Ordinal);

		Assert.Equal("rate_limit", events[7].ErrorCode);
		Assert.DoesNotContain(secret, events[7].ErrorMessage ?? "", StringComparison.Ordinal);
		Assert.False(session.IsConnected);
	}

	[Fact]
	public async Task CloseAsync幂等且DisposeAsync在阻塞接收时无挂起()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
		});

		await session.CloseAsync();
		await session.CloseAsync(); // 幂等
		Assert.False(session.IsConnected);
		Assert.True(transport.CloseCalled);

		List<StreamingSttEvent> tail = [];
		await foreach (StreamingSttEvent evt in session.Events.ReadAllAsync())
		{
			tail.Add(evt);
		}
		Assert.Equal(StreamingSttEventKind.Closed, tail.Last().Kind);

		await session.DisposeAsync();
		await session.DisposeAsync(); // 幂等
		await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AppendAudioAsync(new byte[] { 0, 1 }));
	}

	[Fact]
	public async Task Cancel与DisposeAsync立即终止挂起的接收循环()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
		});

		Task disposeTask = session.DisposeAsync().AsTask();
		Task completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));
		Assert.Same(disposeTask, completed);
		await disposeTask;
		Assert.True(transport.Disposed);
	}

	[Fact]
	public async Task 第一个发送阻塞且第二个发送排队时并发DisposeAsync不会抛ObjectDisposedException()
	{
		TaskCompletionSource firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeRealtimeTransport transport = new()
		{
			BlockSendsAfterSessionUpdate = true,
			FirstAudioSendStarted = firstSendStarted,
		};
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
		});

		Task send1 = session.AppendAudioAsync(new byte[] { 1, 2, 3, 4 });
		await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Task send2 = session.AppendAudioAsync(new byte[] { 5, 6, 7, 8 });
		await Task.Yield();

		Task disposeTask = session.DisposeAsync().AsTask();
		Task finished = await Task.WhenAny(Task.WhenAll(
			Assert.ThrowsAnyAsync<OperationCanceledException>(() => send1),
			Assert.ThrowsAnyAsync<OperationCanceledException>(() => send2),
			disposeTask), Task.Delay(TimeSpan.FromSeconds(2)));

		Assert.NotNull(finished);
		await disposeTask;
		Exception ex1 = await Record.ExceptionAsync(() => send1);
		Exception ex2 = await Record.ExceptionAsync(() => send2);
		Assert.IsAssignableFrom<OperationCanceledException>(ex1);
		Assert.IsAssignableFrom<OperationCanceledException>(ex2);
		Assert.IsNotType<ObjectDisposedException>(ex1);
		Assert.IsNotType<ObjectDisposedException>(ex2);
		Assert.True(transport.Disposed);
	}

	[Fact]
	public async Task 就绪超时覆盖SessionUpdate发送阶段并清理资源()
	{
		FakeRealtimeTransport transport = new()
		{
			BlockAllSends = true,
		};
		OpenAiRealtimeSttProvider provider = new(_config, () => transport);

		VoiceProviderException ex = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			provider.OpenStreamingSessionAsync(new SttRequestOptions
			{
				ApiKey = "sk-test",
				SessionReadyTimeout = TimeSpan.FromMilliseconds(50),
			}));

		Assert.Equal(VoiceFailureKind.Timeout, ex.FailureKind);
		Assert.True(transport.Aborted);
		Assert.True(transport.Disposed);
	}

	[Fact]
	public async Task CloseAsync在发送锁阻塞或握手挂起时按CloseTimeout截止并强制完结通道()
	{
		FakeRealtimeTransport handshakeHangTransport = new()
		{
			HangClose = true,
		};
		handshakeHangTransport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider1 = new(_config, () => handshakeHangTransport);
		await using IStreamingSttSession session1 = await provider1.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
			CloseTimeout = TimeSpan.FromMilliseconds(50),
		});

		await session1.CloseAsync();
		Assert.True(handshakeHangTransport.Aborted);
		Assert.False(session1.IsConnected);
		List<StreamingSttEvent> events1 = [];
		await foreach (StreamingSttEvent evt in session1.Events.ReadAllAsync())
		{
			events1.Add(evt);
		}
		Assert.Equal(StreamingSttEventKind.Closed, events1.Last().Kind);

		// 发送锁被阻塞发送占用时，CloseAsync 依然在 CloseTimeout 后强制中止并完结通道
		TaskCompletionSource firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeRealtimeTransport sendHangTransport = new()
		{
			BlockSendsAfterSessionUpdate = true,
			FirstAudioSendStarted = firstSendStarted,
		};
		sendHangTransport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider2 = new(_config, () => sendHangTransport);
		await using IStreamingSttSession session2 = await provider2.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
			CloseTimeout = TimeSpan.FromMilliseconds(50),
		});

		Task stuckSend = session2.AppendAudioAsync(new byte[] { 1, 2, 3, 4 });
		await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		await session2.CloseAsync();
		Assert.True(sendHangTransport.Aborted);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuckSend);

		List<StreamingSttEvent> events2 = [];
		await foreach (StreamingSttEvent evt in session2.Events.ReadAllAsync())
		{
			events2.Add(evt);
		}
		Assert.Equal(StreamingSttEventKind.Closed, events2.Last().Kind);
	}

	[Fact]
	public async Task 有界事件通道对慢消费者实施背压并防止内存无限增长()
	{
		FakeRealtimeTransport transport = new();
		transport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider provider = new(_config, () => transport);
		await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
			EventChannelCapacity = 2,
			EventBackpressureTimeout = TimeSpan.FromSeconds(2),
		});

		StreamingSttEvent ready = await session.Events.ReadAsync();
		Assert.Equal(StreamingSttEventKind.SessionUpdated, ready.Kind);

		for (int i = 1; i <= 4; i++)
		{
			transport.QueueIncomingText(
				$"{{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_1\",\"content_index\":0,\"delta\":\"d{i}\"}}");
		}
		transport.QueueClose();

		// 留出时间让后台读取填满有界通道，确认不会超出容量 2
		await Task.Delay(50);
		Assert.True(session.Events.CanCount);
		Assert.True(session.Events.Count <= 2);

		List<StreamingSttEvent> received = [];
		await foreach (StreamingSttEvent evt in session.Events.ReadAllAsync())
		{
			received.Add(evt);
		}

		Assert.Equal(["d1", "d2", "d3", "d4"], received.Where(e => e.Kind == StreamingSttEventKind.Delta).Select(e => e.Text!).ToArray());
		Assert.Equal(StreamingSttEventKind.Closed, received.Last().Kind);
	}

	[Fact]
	public async Task 有界事件通道长时间溢出触发Error并保留终态不丢失()
	{
		FakeRealtimeTransport overflowTransport = new();
		overflowTransport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider overflowProvider = new(_config, () => overflowTransport);
		await using IStreamingSttSession overflowSession = await overflowProvider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
			EventChannelCapacity = 3,
			EventBackpressureTimeout = TimeSpan.FromMilliseconds(40),
		});

		_ = await overflowSession.Events.ReadAsync(); // SessionUpdated

		for (int i = 1; i <= 6; i++)
		{
			overflowTransport.QueueIncomingText(
				$"{{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_ov\",\"content_index\":0,\"delta\":\"d{i}\"}}");
		}

		// 等待后台写入完结（溢出触发 Error + Abort + Closed）
		await ((OpenAiRealtimeSttSession)overflowSession).WriterCompleted.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(overflowTransport.Aborted);
		Assert.False(overflowSession.IsConnected);

		List<StreamingSttEvent> overflowEvents = [];
		await foreach (StreamingSttEvent evt in overflowSession.Events.ReadAllAsync())
		{
			overflowEvents.Add(evt);
		}

		Assert.True(overflowEvents.Count <= 3);
		Assert.Contains(overflowEvents, e => e.Kind == StreamingSttEventKind.Error && e.ErrorCode == "event_buffer_overflow");
		Assert.Equal(StreamingSttEventKind.Closed, overflowEvents.Last().Kind);

		// 即使在满载 Delta 后遇到 Completed + Close，Completed 与 Closed 终态事件也绝不静默丢失
		FakeRealtimeTransport terminalTransport = new();
		terminalTransport.QueueIncomingText("{\"type\":\"session.updated\"}");

		OpenAiRealtimeSttProvider terminalProvider = new(_config, () => terminalTransport);
		await using IStreamingSttSession terminalSession = await terminalProvider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			ApiKey = "sk-test",
			EventChannelCapacity = 2,
			EventBackpressureTimeout = TimeSpan.FromMilliseconds(40),
		});

		_ = await terminalSession.Events.ReadAsync(); // SessionUpdated
		terminalTransport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_t\",\"content_index\":0,\"delta\":\"a\"}");
		terminalTransport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_t\",\"content_index\":0,\"delta\":\"b\"}");
		terminalTransport.QueueIncomingText("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"item_t\",\"content_index\":0,\"transcript\":\"最终文本\"}");
		terminalTransport.QueueClose();

		await ((OpenAiRealtimeSttSession)terminalSession).WriterCompleted.WaitAsync(TimeSpan.FromSeconds(2));
		List<StreamingSttEvent> terminalEvents = [];
		await foreach (StreamingSttEvent evt in terminalSession.Events.ReadAllAsync())
		{
			terminalEvents.Add(evt);
		}

		Assert.Contains(terminalEvents, e => e.Kind == StreamingSttEventKind.Completed && e.Text == "最终文本");
		Assert.Equal(StreamingSttEventKind.Closed, terminalEvents.Last().Kind);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_dbPath); } catch (IOException) { }
	}

	private sealed class FakeRealtimeTransport : IRealtimeWebSocketTransport
	{
		private sealed record IncomingItem(WebSocketMessageType MessageType, byte[] Bytes, bool EndOfMessage);

		private readonly Channel<IncomingItem> _incoming = Channel.CreateUnbounded<IncomingItem>();
		private readonly Lock _sendGate = new();
		private IncomingItem? _currentItem;
		private int _currentOffset;
		private int _inFlightSends;

		public WebSocketState State { get; private set; } = WebSocketState.None;
		public Uri? ConnectedUri { get; private set; }
		public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
		public TimeSpan? KeepAliveInterval { get; private set; }
		public List<string> SentTextMessages { get; } = [];
		public int MaxConcurrentSends { get; private set; }
		public bool CloseCalled { get; private set; }
		public bool Aborted { get; private set; }
		public bool Disposed { get; private set; }
		public Exception? ConnectException { get; set; }
		public Exception? SendException { get; set; }
		public TimeSpan SendDelay { get; set; } = TimeSpan.Zero;
		public bool BlockAllSends { get; set; }
		public bool BlockSendsAfterSessionUpdate { get; set; }
		public bool HangClose { get; set; }
		public TaskCompletionSource? FirstAudioSendStarted { get; set; }

		public void QueueIncomingText(string json) =>
			_incoming.Writer.TryWrite(new IncomingItem(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), true));

		public void QueueIncomingFragment(byte[] bytes, bool endOfMessage) =>
			_incoming.Writer.TryWrite(new IncomingItem(WebSocketMessageType.Text, bytes, endOfMessage));

		public void QueueClose() =>
			_incoming.Writer.TryWrite(new IncomingItem(WebSocketMessageType.Close, [], true));

		public void SetRequestHeader(string name, string value) => Headers[name] = value;

		public void ConfigureKeepAlive(TimeSpan keepAliveInterval) => KeepAliveInterval = keepAliveInterval;

		public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (ConnectException is not null) throw ConnectException;
			ConnectedUri = uri;
			State = WebSocketState.Open;
			return Task.CompletedTask;
		}

		public async ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
		{
			int active = Interlocked.Increment(ref _inFlightSends);
			lock (_sendGate)
			{
				if (active > MaxConcurrentSends) MaxConcurrentSends = active;
			}

			try
			{
				string text = Encoding.UTF8.GetString(utf8Json.Span);
				bool isAudioSend = !text.Contains("\"session.update\"", StringComparison.Ordinal);

				if (BlockAllSends || (BlockSendsAfterSessionUpdate && isAudioSend))
				{
					if (isAudioSend) FirstAudioSendStarted?.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
				}

				if (SendException is not null && isAudioSend)
				{
					throw SendException;
				}

				if (SendDelay > TimeSpan.Zero)
				{
					await Task.Delay(SendDelay, cancellationToken).ConfigureAwait(false);
				}

				lock (_sendGate)
				{
					SentTextMessages.Add(text);
				}
			}
			finally
			{
				Interlocked.Decrement(ref _inFlightSends);
			}
		}

		public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		{
			if (_currentItem is null)
			{
				_currentItem = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
				_currentOffset = 0;
			}

			IncomingItem item = _currentItem;
			if (item.MessageType == WebSocketMessageType.Close)
			{
				_currentItem = null;
				State = WebSocketState.CloseReceived;
				return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
			}

			int remaining = item.Bytes.Length - _currentOffset;
			int toCopy = Math.Min(remaining, buffer.Length);
			item.Bytes.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
			_currentOffset += toCopy;

			bool finishedItem = _currentOffset >= item.Bytes.Length;
			bool endOfMessage = finishedItem && item.EndOfMessage;
			if (finishedItem)
			{
				_currentItem = null;
				_currentOffset = 0;
			}

			return new ValueWebSocketReceiveResult(toCopy, item.MessageType, endOfMessage);
		}

		public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			if (HangClose)
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
			}
			CloseCalled = true;
			State = WebSocketState.Closed;
		}

		public void Abort()
		{
			Aborted = true;
			State = WebSocketState.Aborted;
		}

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			_incoming.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}
	}
}
