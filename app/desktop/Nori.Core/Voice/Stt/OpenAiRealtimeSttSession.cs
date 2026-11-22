using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// OpenAI Realtime GA 转写流式会话。
///
/// 使用 <c>session.type = "transcription"</c> 与 <c>audio/pcm</c> 24kHz 协议，
/// 通过有界通道按到达顺序发布增量与完成事件，提供背压与终态保护，并严格屏蔽音频与密钥日志。
/// </summary>
public sealed class OpenAiRealtimeSttSession : IStreamingSttSession
{
	/// <summary>单条服务端 WebSocket 消息的最大重组字节数 (1 MiB)。</summary>
	public const int MaxIncomingMessageBytes = 1024 * 1024;

	private const int ReceiveChunkBytes = 16 * 1024;

	private readonly IRealtimeWebSocketTransport _transport;
	private readonly string _apiKey;
	private readonly TimeSpan _eventBackpressureTimeout;
	private readonly TimeSpan _closeTimeout;
	private readonly BoundedSttEventChannel _events;
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly CancellationTokenSource _sessionCts = new();
	private readonly TaskCompletionSource _sessionReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _sendsDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _closeFinishedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private Task? _receiveTask;
	private long _sequence;
	private volatile bool _isConnected;
	private int _pendingSends;
	private int _closedPublished;
	private int _closeRequested;
	private int _disposed;

	internal OpenAiRealtimeSttSession(
		string providerId,
		IRealtimeWebSocketTransport transport,
		string apiKey,
		int eventChannelCapacity = 256,
		TimeSpan? eventBackpressureTimeout = null,
		TimeSpan? closeTimeout = null)
	{
		ProviderId = providerId;
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_apiKey = apiKey;
		_eventBackpressureTimeout = eventBackpressureTimeout ?? TimeSpan.FromSeconds(5);
		_closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(3);
		_events = new BoundedSttEventChannel(eventChannelCapacity);
	}

	/// <inheritdoc />
	public string ProviderId { get; }

	/// <inheritdoc />
	public bool IsConnected => _isConnected && _disposed == 0;

	/// <inheritdoc />
	public ChannelReader<StreamingSttEvent> Events => _events;

	internal Task WriterCompleted => _events.WriterCompleted;

	internal async Task InitializeAsync(
		Uri endpoint,
		string model,
		string? language,
		TimeSpan connectTimeout,
		TimeSpan sessionReadyTimeout,
		TimeSpan heartbeatInterval,
		CancellationToken cancellationToken)
	{
		if (!string.IsNullOrEmpty(_apiKey))
		{
			_transport.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
		}

		if (heartbeatInterval > TimeSpan.Zero || heartbeatInterval == Timeout.InfiniteTimeSpan)
		{
			_transport.ConfigureKeepAlive(heartbeatInterval);
		}

		await ConnectTransportAsync(endpoint, connectTimeout, cancellationToken).ConfigureAwait(false);

		_receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));

		using CancellationTokenSource readyTimeoutCts = new(sessionReadyTimeout);
		using CancellationTokenSource linkedReadyCts = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			readyTimeoutCts.Token);

		try
		{
			byte[] sessionUpdateBytes = BuildSessionUpdatePayload(model, language);
			await SendJsonInternalAsync(sessionUpdateBytes, linkedReadyCts.Token).ConfigureAwait(false);
			await _sessionReadyTcs.Task.WaitAsync(linkedReadyCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			AbortTransportQuietly();
			throw new VoiceProviderException(
				ProviderId,
				VoiceFailureKind.Timeout,
				"等待实时语音识别会话就绪超时");
		}
	}

	/// <inheritdoc />
	public async Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16Mono24k, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed != 0, this);
		if (pcm16Mono24k.IsEmpty) return;
		if ((pcm16Mono24k.Length & 1) != 0)
		{
			throw new ArgumentException("PCM16 音频字节长度必须为偶数", nameof(pcm16Mono24k));
		}

		string base64 = Convert.ToBase64String(pcm16Mono24k.Span);
		JsonObject payload = new()
		{
			["type"] = "input_audio_buffer.append",
			["audio"] = base64,
		};
		byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

		await SendWhileConnectedAsync(bytes, "发送实时语音音频失败", cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public Task CommitAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed != 0, this);
		byte[] bytes = "{\"type\":\"input_audio_buffer.commit\"}"u8.ToArray();
		return SendWhileConnectedAsync(bytes, "提交实时语音缓冲区失败", cancellationToken);
	}

	/// <inheritdoc />
	public Task CloseAsync(CancellationToken cancellationToken = default)
	{
		if (_disposed != 0) return Task.CompletedTask;

		if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
		{
			return CloseCoreAsync(cancellationToken);
		}

		return cancellationToken.CanBeCanceled
			? _closeFinishedTcs.Task.WaitAsync(cancellationToken)
			: _closeFinishedTcs.Task;
	}

	private async Task CloseCoreAsync(CancellationToken cancellationToken)
	{
		_isConnected = false;

		try
		{
			if (TryEnterSend())
			{
				bool lockAcquired = false;
				try
				{
					using CancellationTokenSource closeTimeoutCts = new(_closeTimeout);
					using CancellationTokenSource linkedCloseCts = CancellationTokenSource.CreateLinkedTokenSource(
						cancellationToken,
						closeTimeoutCts.Token,
						_sessionCts.Token);

					await _sendLock.WaitAsync(linkedCloseCts.Token).ConfigureAwait(false);
					lockAcquired = true;

					if (_transport.State is WebSocketState.Open or WebSocketState.CloseReceived)
					{
						await _transport.CloseAsync(
							WebSocketCloseStatus.NormalClosure,
							"client_closing",
							linkedCloseCts.Token).ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					AbortTransportQuietly();
					await StopReceiveLoopAsync().ConfigureAwait(false);
					throw;
				}
				catch (OperationCanceledException)
				{
					AbortTransportQuietly();
				}
				catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
				{
					AbortTransportQuietly();
				}
				finally
				{
					if (lockAcquired)
					{
						_sendLock.Release();
					}
					ExitSend();
				}
			}

			await StopReceiveLoopAsync().ConfigureAwait(false);
		}
		finally
		{
			_closeFinishedTcs.TrySetResult();
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		_isConnected = false;
		_sessionReadyTcs.TrySetCanceled();

		try { _sessionCts.Cancel(); }
		catch (ObjectDisposedException) { }

		AbortTransportQuietly();

		if (Volatile.Read(ref _pendingSends) == 0)
		{
			_sendsDrainedTcs.TrySetResult();
		}

		await _sendsDrainedTcs.Task.ConfigureAwait(false);
		await StopReceiveLoopAsync().ConfigureAwait(false);

		_closeFinishedTcs.TrySetResult();
		await _transport.DisposeAsync().ConfigureAwait(false);
		_sessionCts.Dispose();
		_sendLock.Dispose();
	}

	internal static byte[] BuildSessionUpdatePayload(string model, string? language)
	{
		JsonObject transcription = new()
		{
			["model"] = model,
		};
		if (!string.IsNullOrWhiteSpace(language))
		{
			transcription["language"] = language.Trim();
		}

		JsonObject payload = new()
		{
			["type"] = "session.update",
			["session"] = new JsonObject
			{
				["type"] = "transcription",
				["audio"] = new JsonObject
				{
					["input"] = new JsonObject
					{
						["format"] = new JsonObject
						{
							["type"] = "audio/pcm",
							["rate"] = 24000,
						},
						["transcription"] = transcription,
						["turn_detection"] = null,
					},
				},
			},
		};

		return Encoding.UTF8.GetBytes(payload.ToJsonString());
	}

	private async Task ConnectTransportAsync(Uri endpoint, TimeSpan connectTimeout, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutCts = new(connectTimeout);
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			timeoutCts.Token);

		try
		{
			await _transport.ConnectAsync(endpoint, linkedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw new VoiceProviderException(
				ProviderId,
				VoiceFailureKind.Timeout,
				"连接实时语音识别服务超时");
		}
		catch (Exception exception)
		{
			string safeMessage = SttSanitizer.RedactError(exception.Message, _apiKey);
			string detail = safeMessage.Length > 0 ? $"连接实时语音识别服务失败: {safeMessage}" : "连接实时语音识别服务失败";
			throw new VoiceProviderException(ProviderId, VoiceFailureKind.Network, detail);
		}
	}

	private async Task SendWhileConnectedAsync(byte[] utf8Json, string failurePrefix, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed != 0, this);
		if (!TryEnterSend())
		{
			throw new ObjectDisposedException(nameof(OpenAiRealtimeSttSession));
		}

		bool lockAcquired = false;
		try
		{
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				_sessionCts.Token);

			await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
			lockAcquired = true;

			if (_disposed != 0)
			{
				throw new OperationCanceledException("实时语音识别会话已释放", _sessionCts.Token);
			}
			if (!_isConnected || _closeRequested != 0)
			{
				throw new InvalidOperationException("实时语音识别会话未连接或已关闭");
			}

			await _transport.SendTextAsync(utf8Json, linkedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
		{
			throw new OperationCanceledException("实时语音识别会话已关闭", _sessionCts.Token);
		}
		catch (Exception ex) when (ex is WebSocketException or IOException)
		{
			_isConnected = false;
			string safeMessage = SttSanitizer.RedactError(ex.Message, _apiKey);
			string detail = safeMessage.Length > 0 ? $"{failurePrefix}: {safeMessage}" : failurePrefix;
			throw new VoiceProviderException(ProviderId, VoiceFailureKind.Network, detail);
		}
		finally
		{
			if (lockAcquired)
			{
				_sendLock.Release();
			}
			ExitSend();
		}
	}

	private async Task SendJsonInternalAsync(byte[] utf8Json, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryEnterSend())
		{
			throw new ObjectDisposedException(nameof(OpenAiRealtimeSttSession));
		}

		bool lockAcquired = false;
		try
		{
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				_sessionCts.Token);

			await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
			lockAcquired = true;

			await _transport.SendTextAsync(utf8Json, linkedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex) when (ex is WebSocketException or IOException)
		{
			string safeMessage = SttSanitizer.RedactError(ex.Message, _apiKey);
			string detail = safeMessage.Length > 0 ? $"发送实时语音会话配置失败: {safeMessage}" : "发送实时语音会话配置失败";
			throw new VoiceProviderException(ProviderId, VoiceFailureKind.Network, detail);
		}
		finally
		{
			if (lockAcquired)
			{
				_sendLock.Release();
			}
			ExitSend();
		}
	}

	private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
	{
		byte[] chunkBuffer = new byte[ReceiveChunkBytes];
		using MemoryStream messageBuffer = new();
		bool skipCloseBackpressureWait = false;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				ValueWebSocketReceiveResult result = await _transport.ReceiveAsync(chunkBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);

				if (result.MessageType == WebSocketMessageType.Close)
				{
					_isConnected = false;
					if (!_sessionReadyTcs.Task.IsCompleted)
					{
						_sessionReadyTcs.TrySetException(new VoiceProviderException(
							ProviderId,
							VoiceFailureKind.Network,
							"实时语音识别连接在会话就绪前已关闭"));
					}
					break;
				}

				if (result.Count > 0)
				{
					if (messageBuffer.Length + result.Count > MaxIncomingMessageBytes)
					{
						_isConnected = false;
						const string tooLargeMessage = "实时语音识别服务端消息超过大小上限";
						if (!_sessionReadyTcs.Task.IsCompleted)
						{
							_sessionReadyTcs.TrySetException(new VoiceProviderException(
								ProviderId,
								VoiceFailureKind.InvalidResponse,
								tooLargeMessage));
						}

						EnqueueTerminalEvent(new StreamingSttEvent
						{
							Kind = StreamingSttEventKind.Error,
							ErrorCode = "message_too_large",
							ErrorType = "protocol_error",
							ErrorMessage = tooLargeMessage,
						});
						AbortTransportQuietly();
						skipCloseBackpressureWait = true;
						break;
					}

					messageBuffer.Write(chunkBuffer, 0, result.Count);
				}

				if (!result.EndOfMessage) continue;

				if (result.MessageType == WebSocketMessageType.Text && messageBuffer.Length > 0)
				{
					ReadOnlyMemory<byte> payload = messageBuffer.GetBuffer().AsMemory(0, (int)messageBuffer.Length);
					PayloadOutcome outcome = await ProcessServerPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
					if (outcome.ConsumerStalled) skipCloseBackpressureWait = true;
					if (!outcome.Continue) break;
				}

				messageBuffer.SetLength(0);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			skipCloseBackpressureWait = true;
		}
		catch (ObjectDisposedException)
		{
			skipCloseBackpressureWait = true;
		}
		catch (Exception ex) when (ex is WebSocketException or IOException)
		{
			_isConnected = false;
			skipCloseBackpressureWait = true;
			string safeMessage = SttSanitizer.RedactError(ex.Message, _apiKey);
			string detail = safeMessage.Length > 0 ? $"实时语音识别连接异常: {safeMessage}" : "实时语音识别连接异常";

			if (!_sessionReadyTcs.Task.IsCompleted)
			{
				_sessionReadyTcs.TrySetException(new VoiceProviderException(
					ProviderId,
					VoiceFailureKind.Network,
					detail));
			}

			EnqueueTerminalEvent(new StreamingSttEvent
			{
				Kind = StreamingSttEventKind.Error,
				ErrorType = "transport_error",
				ErrorMessage = detail,
			});
		}
		finally
		{
			_isConnected = false;
			if (Interlocked.Exchange(ref _closedPublished, 1) == 0)
			{
				StreamingSttEvent closedEvent = new()
				{
					Kind = StreamingSttEventKind.Closed,
					Sequence = Interlocked.Increment(ref _sequence),
				};

				if (skipCloseBackpressureWait || cancellationToken.IsCancellationRequested)
				{
					_events.ForceEnqueueTerminal(closedEvent);
				}
				else
				{
					try
					{
						EventWriteResult closedWrite = await _events.WriteWithWaitAsync(
							closedEvent,
							_eventBackpressureTimeout,
							cancellationToken).ConfigureAwait(false);
						if (closedWrite == EventWriteResult.TimedOut)
						{
							_events.ForceEnqueueTerminal(closedEvent);
						}
					}
					catch (OperationCanceledException)
					{
						_events.ForceEnqueueTerminal(closedEvent);
					}
				}
			}
			_events.Complete();
		}
	}

	private readonly record struct PayloadOutcome(bool Continue, bool ConsumerStalled);

	private async ValueTask<PayloadOutcome> ProcessServerPayloadAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(utf8Json);
		}
		catch (JsonException)
		{
			const string parseError = "服务端返回无效的 JSON 事件";
			if (!_sessionReadyTcs.Task.IsCompleted)
			{
				_sessionReadyTcs.TrySetException(new VoiceProviderException(
					ProviderId,
					VoiceFailureKind.InvalidResponse,
					parseError));
			}

			EnqueueTerminalEvent(new StreamingSttEvent
			{
				Kind = StreamingSttEventKind.Error,
				ErrorType = "invalid_json",
				ErrorMessage = parseError,
			});
			return new PayloadOutcome(true, false);
		}

		StreamingSttEvent? eventToPublish = null;
		bool isTerminalEvent = false;
		bool markSessionReady = false;
		VoiceProviderException? readyError = null;
		bool consumerStalled = false;

		using (doc)
		{
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return new PayloadOutcome(true, false);

			string? eventType = GetOptionalString(root, "type");
			switch (eventType)
			{
				case "session.updated":
					_isConnected = true;
					markSessionReady = true;
					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.SessionUpdated,
					};
					break;

				case "input_audio_buffer.committed":
					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.InputCommitted,
						ItemId = GetOptionalString(root, "item_id"),
					};
					break;

				case "conversation.item.input_audio_transcription.delta":
					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.Delta,
						ItemId = GetOptionalString(root, "item_id"),
						ContentIndex = GetOptionalInt32(root, "content_index"),
						Text = GetOptionalString(root, "delta"),
					};
					break;

				case "conversation.item.input_audio_transcription.completed":
					isTerminalEvent = true;
					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.Completed,
						ItemId = GetOptionalString(root, "item_id"),
						ContentIndex = GetOptionalInt32(root, "content_index"),
						Text = GetOptionalString(root, "transcript"),
					};
					break;

				case "conversation.item.input_audio_transcription.failed":
					isTerminalEvent = true;
					ExtractError(root, out string? failedCode, out string? failedType, out string? failedMessage);
					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.Failed,
						ItemId = GetOptionalString(root, "item_id"),
						ContentIndex = GetOptionalInt32(root, "content_index"),
						ErrorCode = failedCode,
						ErrorType = failedType,
						ErrorMessage = failedMessage,
					};
					break;

				case "error":
					isTerminalEvent = true;
					ExtractError(root, out string? errorCode, out string? errorType, out string? errorMessage);
					string finalMessage = string.IsNullOrWhiteSpace(errorMessage)
						? "实时语音识别服务返回错误"
						: errorMessage;

					if (!_sessionReadyTcs.Task.IsCompleted)
					{
						readyError = new VoiceProviderException(
							ProviderId,
							VoiceFailureKind.ProviderRejected,
							finalMessage);
					}

					eventToPublish = new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.Error,
						ErrorCode = errorCode,
						ErrorType = errorType,
						ErrorMessage = finalMessage,
					};
					break;
			}
		}

		if (eventToPublish is not null)
		{
			long sequence = Interlocked.Increment(ref _sequence);
			StreamingSttEvent sequenced = eventToPublish with { Sequence = sequence };

			if (isTerminalEvent)
			{
				EventWriteResult result = await _events.WriteWithWaitAsync(sequenced, _eventBackpressureTimeout, cancellationToken).ConfigureAwait(false);
				if (result == EventWriteResult.TimedOut)
				{
					_events.ForceEnqueueTerminal(sequenced);
					consumerStalled = true;
				}
			}
			else
			{
				EventWriteResult result = await _events.WriteWithWaitAsync(sequenced, _eventBackpressureTimeout, cancellationToken).ConfigureAwait(false);
				if (result == EventWriteResult.TimedOut)
				{
					_isConnected = false;
					const string overflowMessage = "实时语音识别事件消费过慢，通道已溢出并关闭会话";
					if (!_sessionReadyTcs.Task.IsCompleted)
					{
						_sessionReadyTcs.TrySetException(new VoiceProviderException(
							ProviderId,
							VoiceFailureKind.Timeout,
							overflowMessage));
					}

					EnqueueTerminalEvent(new StreamingSttEvent
					{
						Kind = StreamingSttEventKind.Error,
						ErrorCode = "event_buffer_overflow",
						ErrorType = "backpressure_error",
						ErrorMessage = overflowMessage,
					});
					AbortTransportQuietly();
					return new PayloadOutcome(false, true);
				}
				if (result == EventWriteResult.Completed)
				{
					return new PayloadOutcome(false, false);
				}
			}
		}

		if (markSessionReady)
		{
			_sessionReadyTcs.TrySetResult();
		}
		else if (readyError is not null)
		{
			_sessionReadyTcs.TrySetException(readyError);
		}

		return new PayloadOutcome(true, consumerStalled);
	}

	private void EnqueueTerminalEvent(StreamingSttEvent item)
	{
		long sequence = Interlocked.Increment(ref _sequence);
		_events.ForceEnqueueTerminal(item with { Sequence = sequence });
	}

	private void ExtractError(JsonElement root, out string? code, out string? type, out string? message)
	{
		code = null;
		type = null;
		message = null;

		if (!root.TryGetProperty("error", out JsonElement errorElement) || errorElement.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		code = GetOptionalString(errorElement, "code");
		type = GetOptionalString(errorElement, "type");
		string? rawMessage = GetOptionalString(errorElement, "message");
		if (!string.IsNullOrWhiteSpace(rawMessage))
		{
			message = SttSanitizer.RedactError(rawMessage, _apiKey);
		}
	}

	private bool TryEnterSend()
	{
		while (true)
		{
			int current = Volatile.Read(ref _pendingSends);
			if (Volatile.Read(ref _disposed) != 0) return false;
			if (Interlocked.CompareExchange(ref _pendingSends, current + 1, current) == current)
			{
				if (Volatile.Read(ref _disposed) != 0)
				{
					ExitSend();
					return false;
				}
				return true;
			}
		}
	}

	private void ExitSend()
	{
		if (Interlocked.Decrement(ref _pendingSends) == 0 && Volatile.Read(ref _disposed) != 0)
		{
			_sendsDrainedTcs.TrySetResult();
		}
	}

	private void AbortTransportQuietly()
	{
		try
		{
			if (_transport.State is WebSocketState.Open or WebSocketState.Connecting or WebSocketState.CloseSent or WebSocketState.CloseReceived)
			{
				_transport.Abort();
			}
		}
		catch (ObjectDisposedException)
		{
			// 传输层已被释放时忽略
		}
	}

	private async Task StopReceiveLoopAsync()
	{
		try { _sessionCts.Cancel(); }
		catch (ObjectDisposedException) { }

		if (_receiveTask is { } receiveTask)
		{
			try
			{
				await receiveTask.ConfigureAwait(false);
			}
			catch
			{
				// 接收循环异常已在循环内部转为事件并完成通道
			}
		}
		else
		{
			_events.Complete();
		}
	}

	private static string? GetOptionalString(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static int? GetOptionalInt32(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out JsonElement value)
		&& value.ValueKind == JsonValueKind.Number
		&& value.TryGetInt32(out int number)
			? number
			: null;
}
