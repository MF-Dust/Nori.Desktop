using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nori.Core.Voice;
using Nori.Desktop.Telemetry;
using Sentry;
using Sentry.Protocol;
using Sentry.Protocol.Envelopes;

namespace Nori.Desktop.Tests;

/// <summary>
/// Native Sentry 遥测测试。
///
/// 通过内部 BeforeSend 观测缝在发送边界观察最终事件 (返回 null 丢弃, 不出网),
/// 验证 CaptureException 的 handled/terminal 语义与 tag 白名单, 而不是只验证方法被调用。
/// </summary>
public sealed class SentryTelemetryTests
{
	[Fact]
	public void 会话仅在启用时开始且关闭重开与释放都只结束一次()
	{
		RecordingTransport transport = new();
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test")
		{
			TestTransport = transport,
		};
		telemetry.Configure(false);
		Assert.Empty(transport.Sessions);

		telemetry.Configure(true);
		Assert.True(SentrySdk.IsSessionActive);
		telemetry.Configure(true);
		telemetry.Configure(false);
		Assert.False(SentrySdk.IsSessionActive);
		telemetry.Configure(false);

		SessionUpdate[] first = transport.Sessions.ToArray();
		Assert.Equal(2, first.Length);
		Assert.True(first[0].IsInitial);
		Assert.Equal("nori@test", first[0].Release);
		Assert.Equal("test", first[0].Environment);
		Assert.Null(first[0].IpAddress);
		Assert.Null(first[0].UserAgent);
		Assert.Equal(first[0].Id, first[1].Id);
		Assert.Equal(SessionEndStatus.Exited, first[1].EndStatus);

		telemetry.Configure(true);
		telemetry.Dispose();
		telemetry.Dispose();
		telemetry.Configure(true);
		Assert.False(telemetry.IsEnabled);
		SessionUpdate[] all = transport.Sessions.ToArray();
		Assert.Equal(4, all.Length);
		Assert.True(all[2].IsInitial);
		Assert.NotEqual(first[0].Id, all[2].Id);
		Assert.Equal(all[2].Id, all[3].Id);
		Assert.Equal(SessionEndStatus.Exited, all[3].EndStatus);
	}

	[Theory]
	[InlineData(true, false, SessionEndStatus.Exited, 1)]
	[InlineData(false, false, SessionEndStatus.Unhandled, 0)]
	[InlineData(false, true, SessionEndStatus.Crashed, 1)]
	public void 异常会话状态在释放时不会被覆盖(bool handled, bool terminal, SessionEndStatus expected, int expectedErrors)
	{
		RecordingTransport transport = new();
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test")
		{
			TestTransport = transport,
		};
		telemetry.Configure(true);
		telemetry.CaptureException(new InvalidOperationException("私密内容"), "bridge.test", handled, terminal);
		telemetry.Dispose();

		SessionUpdate end = Assert.Single(transport.Sessions, update => update.EndStatus is not null);
		Assert.Equal(expected, end.EndStatus);
		Assert.Equal(expectedErrors, end.ErrorCount);
	}

	private sealed class RecordingTransport : Sentry.Extensibility.ITransport
	{
		public ConcurrentQueue<SessionUpdate> Sessions { get; } = new();

		public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
		{
			foreach (EnvelopeItem item in envelope.Items)
			{
				if (item.TryGetType() != "session") continue;
				using MemoryStream payload = new();
				item.Payload.Serialize(payload, null);
				payload.Position = 0;
				using JsonDocument document = JsonDocument.Parse(payload);
				Sessions.Enqueue(SessionUpdate.FromJson(document.RootElement));
			}
			return Task.CompletedTask;
		}
	}
	[Theory]
	[InlineData(true, false)]
	[InlineData(false, false)]
	[InlineData(false, true)]
	public void CaptureException的handled与terminal映射到发送边界事件(bool handled, bool terminal)
	{
		List<SentryEvent> captured = [];
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test");
		telemetry.Configure(true);
		Assert.True(telemetry.IsEnabled);
		telemetry.TestBeforeSend = capturedEvent =>
		{
			captured.Add(capturedEvent);
			return null; // 丢弃事件, 测试不出网
		};

		telemetry.CaptureException(new InvalidOperationException("测试消息"), "bridge.test", handled, terminal);

		SentryEvent sentryEvent = Assert.Single(captured);
		SentryException? sentryException = sentryEvent.SentryExceptions?.SingleOrDefault();
		Assert.NotNull(sentryException);
		if (handled)
		{
			// SDK 对默认 handled=true 不附加 mechanism; Sentry 协议缺省即为 handled=true 且非 terminal。
			Assert.NotEqual(false, sentryException.Mechanism?.Handled);
			Assert.NotEqual(true, sentryException.Mechanism?.Terminal);
		}
		else
		{
			Assert.False(sentryException.Mechanism?.Handled);
			Assert.Equal(terminal, sentryException.Mechanism?.Terminal);
		}
	}

	[Fact]
	public void 默认CaptureException标记为handled且operation标签存活()
	{
		List<SentryEvent> captured = [];
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test");
		telemetry.Configure(true);
		telemetry.TestBeforeSend = capturedEvent =>
		{
			captured.Add(capturedEvent);
			return null;
		};

		telemetry.CaptureException(new InvalidOperationException("聊天内容"), "bridge.chat_start");

		SentryEvent sentryEvent = Assert.Single(captured);
		SentryException? sentryException = sentryEvent.SentryExceptions?.SingleOrDefault();
		Assert.NotNull(sentryException);
		Assert.True(sentryException!.Mechanism?.Handled);
		// ScrubEvent 清空全部标签后白名单键必须存活, 否则生产事件无法按 operation 检索。
		Assert.Equal("bridge.chat_start", sentryEvent.Tags?["operation"]);
		Assert.Equal("native", sentryEvent.Tags?["runtime"]);
	}

	[Fact]
	public void 白名单外的标签与值会被丢弃或归一化()
	{
		List<SentryEvent> captured = [];
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test");
		telemetry.Configure(true);
		telemetry.TestBeforeSend = capturedEvent =>
		{
			captured.Add(capturedEvent);
			return null;
		};

		telemetry.CaptureException(new InvalidOperationException("测试消息"), "bridge.test", tags: new Dictionary<string, string>
		{
			["failure_kind"] = "HTTP_Status",
			["user_content"] = "用户的聊天正文",
		});

		SentryEvent sentryEvent = Assert.Single(captured);
		Assert.Equal("http_status", sentryEvent.Tags?["failure_kind"]);
		Assert.False(sentryEvent.Tags?.ContainsKey("user_content"));
	}

	[Fact]
	public async Task 无DSN时启停和事务都不会出网()
	{
		RecordingTransport transport = new();
		using SentryTelemetry telemetry = new("", "nori@test", "test") { TestTransport = transport };

		Assert.False(telemetry.IsAvailable);
		telemetry.Configure(true);
		Assert.False(telemetry.IsEnabled);
		using (telemetry.StartTransaction("bridge.chat_start"))
		{
			telemetry.CaptureException(new InvalidOperationException("聊天内容"), "bridge.chat_start");
		}
		telemetry.CaptureException(new InvalidOperationException("聊天内容"), "bridge.chat_start",
			tags: new Dictionary<string, string> { ["failure_kind"] = "timeout" });
		await telemetry.FlushAsync(TimeSpan.FromMilliseconds(10));
		Assert.Empty(transport.Sessions);
	}

	[Fact]
	public void 连接超时与关闭噪声在发送边界之前丢弃()
	{
		List<SentryEvent> captured = [];
		using SentryTelemetry telemetry = new("https://publickey@sentry.invalid/1", "nori@test", "test");
		telemetry.Configure(true);
		telemetry.TestBeforeSend = capturedEvent =>
		{
			captured.Add(capturedEvent);
			return null;
		};

		telemetry.CaptureException(new TaskCanceledException("超时"), "voice.tts_test");
		telemetry.CaptureException(new TimeoutException("超时"), "voice.tts_test");
		telemetry.CaptureException(new SocketException((int)SocketError.ConnectionRefused), "rgb.connect");
		telemetry.CaptureException(
			new HttpRequestException("连接失败", new SocketException((int)SocketError.ConnectionRefused)),
			"voice.tts_test");
		telemetry.CaptureException(
			new VoiceProviderException("openai", VoiceFailureKind.Network, "网络失败"),
			"voice.tts_test");
		telemetry.CaptureException(
			new VoiceProviderException("gemini", VoiceFailureKind.HttpRejected, "HTTP 429", httpStatusCode: 429),
			"voice.tts_test");
		telemetry.CaptureException(new InvalidOperationException("Application is already shutting down."), "ui.shutdown");
		telemetry.CaptureException(new AggregateException(new TaskCanceledException(), new SocketException()), "runtime.background_task");
		Assert.Empty(captured);

		telemetry.CaptureException(
			new HttpRequestException("状态", inner: null, HttpStatusCode.TooManyRequests),
			"voice.tts_test");
		telemetry.CaptureException(
			new VoiceProviderException("minimax", VoiceFailureKind.ProviderRejected, "拒绝"),
			"voice.tts_test");
		telemetry.CaptureException(new InvalidOperationException("别的错误"), "bridge.test");
		telemetry.CaptureException(
			new AggregateException(new TaskCanceledException(), new InvalidOperationException("bug")),
			"runtime.background_task");
		Assert.Equal(4, captured.Count);
	}
}
