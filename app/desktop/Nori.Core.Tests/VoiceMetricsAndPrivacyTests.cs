using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Voice;
using Nori.Core.Voice.Metrics;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

public sealed class VoiceMetricsAndPrivacyTests
{
	private const string ApiKey = "sk-proj-G4nKfC27KJr4q1Vb6Yz9sUa3";
	private const string Transcript = "a private sentence from the recognizer";
	private const string Echo = $"api_key={ApiKey}; {Transcript}";

	[Fact]
	public void CaptureSnapshot_EmptyAndNearestRankPercentiles()
	{
		VoiceMetricsTracker tracker = new();
		VoiceMetricsSnapshot empty = tracker.CaptureSnapshot();
		Assert.Equal(new(default, default, default, default, 0, default, 0, 0), empty);
		for (int i = 20; i > 0; i--)
		{
			tracker.RecordFirstDeltaLatency(TimeSpan.FromMilliseconds(i));
			tracker.RecordFinalTranscriptLatency(TimeSpan.FromMilliseconds(i * 2));
			tracker.RecordAgentFirstTokenLatency(TimeSpan.FromMilliseconds(i * 3));
			tracker.RecordTtsFirstSegmentLatency(TimeSpan.FromMilliseconds(i * 4));
		}
		VoiceMetricsSnapshot snapshot = tracker.CaptureSnapshot();
		Assert.Equal(new(10, 19, 20, 20), snapshot.FirstDeltaLatencyMs);
		Assert.Equal(new(20, 38, 40, 20), snapshot.FinalTranscriptLatencyMs);
		Assert.Equal(new(30, 57, 60, 20), snapshot.AgentFirstTokenLatencyMs);
		Assert.Equal(new(40, 76, 80, 20), snapshot.TtsFirstSegmentLatencyMs);
		Assert.Equal(0, empty.FirstDeltaLatencyMs.Count);
	}

	[Fact]
	public void CaptureSnapshot_EvictsOldestAndRecordsConcurrentObservations()
	{
		VoiceMetricsTracker tracker = new();
		tracker.RecordFirstDeltaLatency(TimeSpan.FromSeconds(100));
		for (int i = 1; i <= 512; i++) tracker.RecordFirstDeltaLatency(TimeSpan.FromMilliseconds(i));
		Parallel.For(1, 513, i =>
		{
			tracker.RecordFinalTranscriptLatency(TimeSpan.FromMilliseconds(25));
			tracker.ObserveAudioBufferBytes(i);
			tracker.RecordDeviceRecoveryFailure();
			Assert.InRange(tracker.CaptureSnapshot().FinalTranscriptLatencyMs.Count, 1, 256);
		});
		tracker.ObserveProcessResources(800, 25.5);
		tracker.ObserveProcessResources(600, 8.5);
		VoiceMetricsSnapshot snapshot = tracker.CaptureSnapshot();
		Assert.Equal(new(384, 500, 512, 256), snapshot.FirstDeltaLatencyMs);
		Assert.Equal(new(25, 25, 25, 256), snapshot.FinalTranscriptLatencyMs);
		Assert.Equal(512, snapshot.PeakAudioBufferBytes);
		Assert.Equal(new(600, 800), snapshot.WorkingSetBytes);
		Assert.Equal(8.5, snapshot.CpuUtilizationPercent);
		Assert.Equal(512, snapshot.DeviceRecoveryFailureCount);

		using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
		Assert.Equal(new[] { "FirstDeltaLatencyMs", "FinalTranscriptLatencyMs", "AgentFirstTokenLatencyMs", "TtsFirstSegmentLatencyMs",
			"PeakAudioBufferBytes", "WorkingSetBytes", "CpuUtilizationPercent", "DeviceRecoveryFailureCount" },
			json.RootElement.EnumerateObject().Select(p => p.Name));
		foreach (JsonProperty property in json.RootElement.EnumerateObject())
		{
			if (property.Value.ValueKind == JsonValueKind.Object)
				Assert.All(property.Value.EnumerateObject(), field => Assert.Equal(JsonValueKind.Number, field.Value.ValueKind));
			else Assert.Equal(JsonValueKind.Number, property.Value.ValueKind);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task BatchStt_DoesNotEchoProviderBodyOrTransportException(bool whisper)
	{
		string directory = Directory.CreateTempSubdirectory("nori-stt-privacy-").FullName;
		try
		{
			using NoriDatabase database = NoriDatabase.Open(Path.Combine(directory, "nori.db"));
			ConfigStore config = new(database);
			using FileLogger logger = new(Path.Combine(directory, "logs"));
			using HttpClient client = new(new RejectingHttpHandler());
			ISttProvider provider = whisper ? new WhisperSttProvider(client, config) : new GptTranscribeProvider(client, config);
			RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");
			VoiceProviderException rejected = await Assert.ThrowsAsync<VoiceProviderException>(() =>
				provider.TranscribeAsync(audio, new SttRequestOptions { ApiKey = ApiKey }));
			Assert.Equal(401, rejected.HttpStatusCode);
			AssertPrivateFailure(logger, rejected);
			VoiceProviderException transport = await Assert.ThrowsAsync<VoiceProviderException>(() =>
				provider.TranscribeAsync(audio, new SttRequestOptions { ApiKey = ApiKey }));
			Assert.Equal(VoiceFailureKind.Network, transport.FailureKind);
			AssertPrivateFailure(logger, transport);
		}
		finally { Directory.Delete(directory, recursive: true); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StreamingStt_DoesNotEchoProviderBodyOrTransportException(bool transportFailure)
	{
		string directory = Directory.CreateTempSubdirectory("nori-stt-privacy-").FullName;
		try
		{
			using FileLogger logger = new(directory);
			OpenAiRealtimeSttProvider provider = new(transportFactory: () => new RejectingWebSocket(transportFailure));
			VoiceProviderException failure = await Assert.ThrowsAsync<VoiceProviderException>(() =>
				provider.OpenStreamingSessionAsync(new SttRequestOptions { ApiKey = ApiKey, SessionReadyTimeout = TimeSpan.FromSeconds(2) }));
			Assert.Equal(transportFailure ? VoiceFailureKind.Network : VoiceFailureKind.ProviderRejected, failure.FailureKind);
			AssertPrivateFailure(logger, failure);
		}
		finally { Directory.Delete(directory, recursive: true); }
	}

	private static void AssertPrivateFailure(FileLogger logger, VoiceProviderException failure)
	{
		Assert.DoesNotContain(ApiKey, failure.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain(Transcript, failure.ToString(), StringComparison.Ordinal);
		logger.Write(LogSource.Backend, "warn", failure.Message, exception: new IOException(Echo));
		LogEntry entry = logger.RecentLogs()[^1];
		Assert.Equal(failure.Message, entry.Message);
		string serialized = JsonSerializer.Serialize(entry);
		Assert.DoesNotContain(ApiKey, serialized, StringComparison.Ordinal);
		Assert.DoesNotContain(Transcript, serialized, StringComparison.Ordinal);
	}

	private sealed class RejectingHttpHandler : HttpMessageHandler
	{
		private int _requests;
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Interlocked.Increment(ref _requests) == 1
				? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(Echo) })
				: Task.FromException<HttpResponseMessage>(new HttpRequestException(Echo));
	}

	private sealed class RejectingWebSocket(bool failConnect) : IRealtimeWebSocketTransport
	{
		private bool _received;
		public WebSocketState State { get; private set; }
		public void SetRequestHeader(string name, string value) { }
		public void ConfigureKeepAlive(TimeSpan keepAliveInterval) { }
		public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
		{
			if (failConnect) return Task.FromException(new WebSocketException(Echo));
			State = WebSocketState.Open;
			return Task.CompletedTask;
		}
		public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		{
			if (_received) return ValueTask.FromResult(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));
			_received = true;
			string error = JsonSerializer.Serialize(new { type = "error", error = new { code = ApiKey, type = Transcript, message = Echo } });
			int count = Encoding.UTF8.GetBytes(error, buffer.Span);
			return ValueTask.FromResult(new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, true));
		}
		public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public void Abort() => State = WebSocketState.Aborted;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
