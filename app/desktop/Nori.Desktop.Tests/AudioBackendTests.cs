using System.Text.Json;
using Nori.Core.Voice;
using Nori.Desktop.Audio;

namespace Nori.Desktop.Tests;

/// <summary>隐藏 main WebView 时音频通道仍可用的可注入后端测试。</summary>
public class AudioBackendTests
{
	[Theory]
	[InlineData("audio/wav")]
	[InlineData("audio/mpeg")]
	[InlineData("audio/ogg")]
	[InlineData("audio/webm;codecs=opus")]
	public async Task WebView保留实际格式且播放完成后通知状态(string mime)
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewAudioPlayback playback = null!;
		List<bool> states = [];
		channel.Posted = (name, payload) =>
		{
			if (name != "nori:audio-play") return;
			JsonElement message = JsonSerializer.SerializeToElement(payload!);
			string token = message.GetProperty("token").GetString()!;
			Assert.Equal(mime, message.GetProperty("mime").GetString());
			Assert.True(media.TryTakeAudio(token, out byte[] bytes, out string actualMime));
			Assert.Equal(new byte[] {1, 2}, bytes);
			Assert.Equal(mime, actualMime);
			playback.ReportPlaybackFinished(token, null);
		};
		playback = new WebViewAudioPlayback(media, token => token, channel);
		playback.PlayingChanged += states.Add;

		await playback.PlayAsync(new EncodedAudio([1, 2], mime), CancellationToken.None);

		Assert.False(playback.IsPlaying);
		Assert.Equal([true, false], states);
	}

	[Fact]
	public async Task 麦克风权限失败立即结束而非等待上传超时()
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewMicrophoneRecorder recorder = new(media, token => token, channel);
		TaskCompletionSource<string> startPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		channel.Posted = (name, payload) =>
		{
			if (name == "nori:audio-record-start")
			{
				startPosted.TrySetResult(JsonSerializer.SerializeToElement(payload!).GetProperty("token").GetString()!);
			}
		};

		Task start = recorder.StartAsync();
		string token = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		recorder.ReportRecordingFailed(token, "用户拒绝麦克风权限");

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
		Assert.Contains("用户拒绝", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 录音上传保留实际MIME和文件名()
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewMicrophoneRecorder recorder = new(media, token => token, channel);
		TaskCompletionSource<string> startPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> stopPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		channel.Posted = (name, payload) =>
		{
			string token = JsonSerializer.SerializeToElement(payload!).GetProperty("token").GetString()!;
			if (name == "nori:audio-record-start") startPosted.TrySetResult(token);
			if (name == "nori:audio-record-stop") stopPosted.TrySetResult(token);
		};

		Task start = recorder.StartAsync();
		string token = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		recorder.ReportRecordingReady(token);
		await start;

		Task<RecordedAudio> stop = recorder.StopAsync();
		Assert.Equal(token, await stopPosted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
		Assert.True(media.TryCompleteUpload(token, new RecordedAudio([3, 4], "audio/webm;codecs=opus", "speech.webm")));
		RecordedAudio audio = await stop;
		Assert.Equal("audio/webm;codecs=opus", audio.Mime);
		Assert.Equal("speech.webm", audio.FileName);
	}

	[Fact]
	public async Task 就绪前Stop返回空录音并解除Start等待()
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewMicrophoneRecorder recorder = new(media, token => token, channel);
		TaskCompletionSource<string> startPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		channel.Posted = (name, payload) =>
		{
			if (name == "nori:audio-record-start")
				startPosted.TrySetResult(JsonSerializer.SerializeToElement(payload!).GetProperty("token").GetString()!);
		};

		Task start = recorder.StartAsync();
		string token = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));

		RecordedAudio audio = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));

		// 空录音 + StartAsync 以取消结束 + 票据作废, 旧 token 的后续回报被忽略。
		Assert.Empty(audio.Bytes);
		Assert.Equal("audio/wav", audio.Mime);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
		Assert.False(media.TryCompleteUpload(token, new RecordedAudio([1], "audio/wav", "a.wav")));
		Assert.False(recorder.IsRecording);
	}

	[Fact]
	public async Task 重复Stop幂等返回空录音()
	{
		FakeChannel channel = new();
		WebViewMicrophoneRecorder recorder = new(new MediaExchange(), token => token, channel);

		RecordedAudio first = await recorder.StopAsync();
		RecordedAudio second = await recorder.StopAsync();

		Assert.Empty(first.Bytes);
		Assert.Empty(second.Bytes);
	}

	[Fact]
	public async Task 取消后的晚到上传不破坏新一轮录音()
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewMicrophoneRecorder recorder = new(media, token => token, channel);
		TaskCompletionSource<string> startPosted = NewTcs();
		channel.Posted = (name, payload) =>
		{
			if (name == "nori:audio-record-start")
				startPosted.TrySetResult(JsonSerializer.SerializeToElement(payload!).GetProperty("token").GetString()!);
		};

		// 第一轮: start → ready → stop 中途取消
		Task firstStart = recorder.StartAsync();
		string firstToken = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		recorder.ReportRecordingReady(firstToken);
		await firstStart.WaitAsync(TimeSpan.FromSeconds(1));

		using CancellationTokenSource stopCts = new();
		Task<RecordedAudio> stopped = recorder.StopAsync(stopCts.Token);
		await stopCts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);

		// 晚到上传对已取消票据无效
		Assert.False(media.TryCompleteUpload(firstToken, new RecordedAudio([9], "audio/wav", "late.wav")));

		// 第二轮不受影响
		startPosted = NewTcs();
		Task secondStart = recorder.StartAsync();
		string secondToken = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		Assert.NotEqual(firstToken, secondToken);
		recorder.ReportRecordingReady(secondToken);
		await secondStart.WaitAsync(TimeSpan.FromSeconds(1));

		Task<RecordedAudio> stopped2 = recorder.StopAsync();
		string stopToken = JsonSerializer.SerializeToElement(channel.LastStopPayload!)
			.GetProperty("token").GetString()!;
		Assert.Equal(secondToken, stopToken);
		Assert.True(media.TryCompleteUpload(stopToken, new RecordedAudio([5, 6], "audio/wav", "round2.wav")));
		RecordedAudio second = await stopped2;
		Assert.Equal([5, 6], second.Bytes);
	}

	private static TaskCompletionSource<string> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	[Fact]
	public async Task 录音中Dispose解除Start等待并作废票据()
	{
		FakeChannel channel = new();
		MediaExchange media = new();
		WebViewMicrophoneRecorder recorder = new(media, token => token, channel);
		TaskCompletionSource<string> startPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		channel.Posted = (name, payload) =>
		{
			if (name == "nori:audio-record-start")
				startPosted.TrySetResult(JsonSerializer.SerializeToElement(payload!).GetProperty("token").GetString()!);
		};

		Task start = recorder.StartAsync();
		string token = await startPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));

		recorder.Dispose();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
		Assert.False(media.TryCompleteUpload(token, new RecordedAudio([1], "audio/wav", "a.wav")));
	}

	private sealed class FakeChannel : IAudioHostChannel
	{
		public bool IsAvailable => true;
		public Action<string, object?>? Posted { get; set; }
		public object? LastStopPayload { get; private set; }
		public void Post(string name, object? payload)
		{
			if (name == "nori:audio-record-stop") LastStopPayload = payload;
			Posted?.Invoke(name, payload);
		}
	}
}
