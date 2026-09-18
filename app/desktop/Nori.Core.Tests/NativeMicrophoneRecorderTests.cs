using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>
/// 原生录音的编排层。
///
/// 和播放那边一样：只有设备需要原生实现，攒缓冲、下混、重采样、封 WAV 都是普通
/// 托管代码，用假设备测完。这一族尤其要盯住**产出的格式** —— 它直接决定上传给
/// Whisper 的体积，也决定识别质量。
/// </summary>
public sealed class NativeMicrophoneRecorderTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	/// <summary>通过明确的信号控制打开、样本耗尽及读取退出，不猜测线程调度。</summary>
	private sealed class FakeMicrophone(AudioFormat format, float[] samples) : IAudioCaptureDevice
	{
		private readonly ManualResetEventSlim _openReleased = new(false);
		private readonly ManualResetEventSlim _readReleased = new(false);
		private int _at;
		private int _reading;
		private volatile bool _stopped;

		public bool Opened { get; private set; }
		public int DisposeCount { get; private set; }
		public bool DisposedOnce => DisposeCount == 1;
		public bool DisposedDuringRead { get; private set; }
		public int ReadCount { get; private set; }
		public bool BlockWhenDrained { get; init; }
		public bool BlockOpen { get; init; }
		public bool FailOpen { get; init; }
		public bool FailRead { get; init; }
		public bool HoldReadAfterStop { get; init; }
		public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public AudioFormat Open()
		{
			OpenEntered.TrySetResult();
			if (BlockOpen) Assert.True(_openReleased.Wait(Timeout), "未放行麦克风打开");
			if (FailOpen) throw new AudioDeviceException("打开麦克风失败");
			Opened = true;
			return format;
		}

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			Interlocked.Exchange(ref _reading, 1);
			try
			{
				ReadCount++;
				if (FailRead) throw new AudioDeviceException("读取麦克风失败");
				if (_stopped) return 0;
				if (_at < samples.Length)
				{
					int take = Math.Min(buffer.Length, samples.Length - _at);
					samples.AsSpan(_at, take).CopyTo(buffer);
					_at += take;
					return take;
				}

				// 下一次 Read 才发出耗尽信号，确保上一批已被录音器收集。
				Drained.TrySetResult();
				if (!BlockWhenDrained) return 0;
				using CancellationTokenRegistration registration = cancellationToken.Register(() =>
				{
					ReadCancelled.TrySetResult();
					if (!HoldReadAfterStop) _readReleased.Set();
				});
				ReadBlocked.TrySetResult();
				Assert.True(_readReleased.Wait(Timeout), "未放行麦克风读取");
				cancellationToken.ThrowIfCancellationRequested();
				return 0;
			}
			finally
			{
				Interlocked.Exchange(ref _reading, 0);
			}
		}

		public void Stop()
		{
			_stopped = true;
			if (!HoldReadAfterStop) _readReleased.Set();
		}

		public void ReleaseOpen() => _openReleased.Set();
		public void ReleaseRead() => _readReleased.Set();

		public void Dispose()
		{
			DisposeCount++;
			DisposedDuringRead = Volatile.Read(ref _reading) != 0;
			_openReleased.Dispose();
			_readReleased.Dispose();
			Disposed.TrySetResult();
		}
	}

	private static float[] Tone(int frames, int channels, float amplitude = 0.4f)
	{
		float[] samples = new float[frames * channels];
		for (int index = 0; index < samples.Length; index++)
			samples[index] = amplitude * MathF.Sin(index * 0.03f);
		return samples;
	}

	[Fact]
	public async Task 录完产出十六千单声道的WAV()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 2), Tone(48000, 2));
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await microphone.Drained.Task.WaitAsync(Timeout);
		RecordedAudio recorded = await recorder.StopAsync();

		Assert.Equal("audio/wav", recorded.Mime);
		Assert.EndsWith(".wav", recorded.FileName, StringComparison.Ordinal);

		PcmAudio decoded = WaveDecoder.Decode(recorded.Bytes);
		Assert.Equal(NativeMicrophoneRecorder.TargetSampleRate, decoded.SampleRate);
		Assert.Equal(1, decoded.Channels);
		Assert.True(microphone.Opened);
		Assert.True(microphone.DisposedOnce);
	}

	/// <summary>
	/// 体积是这一步的重点：48 kHz 立体声直传的话，30 秒就是 5.6 MB。
	/// 降到 16 kHz 单声道之后应当只有约九分之一。
	/// </summary>
	[Fact]
	public async Task 上传体积按十六千单声道算()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 2), Tone(48000, 2));   // 1 秒
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await microphone.Drained.Task.WaitAsync(Timeout);
		RecordedAudio recorded = await recorder.StopAsync();

		// 样本耗尽信号保证完整收集一秒，输出长度应当精确匹配。
		Assert.Equal(32044, recorded.Bytes.Length);
	}

	[Fact]
	public async Task 录音状态起落()
	{
		FakeMicrophone microphone = new(new AudioFormat(44100, 1), Tone(4410, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		Assert.False(recorder.IsRecording);
		await recorder.StartAsync();
		Assert.True(recorder.IsRecording);

		await recorder.StopAsync();
		Assert.False(recorder.IsRecording);
	}

	/// <summary>Stop 要能把**卡在 Read 里**的采集放出来，而不是等它超时。</summary>
	[Fact]
	public async Task 停止能打断等待中的读取()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), Tone(4800, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await microphone.ReadBlocked.Task.WaitAsync(Timeout);

		Task<RecordedAudio> stopping = recorder.StopAsync();
		RecordedAudio recorded = await stopping.WaitAsync(Timeout);

		Assert.Equal(3244, recorded.Bytes.Length);
		Assert.True(microphone.ReadCancelled.Task.IsCompletedSuccessfully);
		Assert.True(microphone.DisposedOnce);
		Assert.False(microphone.DisposedDuringRead);
	}

	[Fact]
	public async Task 没在录的时候停会报错()
	{
		using NativeMicrophoneRecorder recorder = new(() => new FakeMicrophone(new AudioFormat(48000, 1), []));

		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
	}

	[Fact]
	public async Task 重复开始会报错()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), Tone(4800, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		await recorder.StopAsync();
	}

	/// <summary>一段没说话的录音也要是合法 WAV，不能让上传端拿到空文件。</summary>
	[Fact]
	public async Task 没采到样本也产出合法WAV()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []);
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		RecordedAudio recorded = await recorder.StopAsync();

		Assert.Equal(44, recorded.Bytes.Length);
		Assert.True(WaveDecoder.IsWave(recorded.Bytes));
		PcmAudio decoded = WaveDecoder.Decode(recorded.Bytes);
		Assert.Equal(NativeMicrophoneRecorder.TargetSampleRate, decoded.SampleRate);
	}

	[Fact]
	public async Task 打开设备期间第二次开始不能创建另一个设备()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []) {BlockOpen = true};
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() =>
		{
			Interlocked.Increment(ref created);
			return microphone;
		});

		Task starting = recorder.StartAsync();
		await microphone.OpenEntered.Task.WaitAsync(Timeout);
		Assert.True(recorder.IsRecording);
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		Assert.Equal(1, Volatile.Read(ref created));
		microphone.ReleaseOpen();
		await starting.WaitAsync(Timeout);
		await recorder.StopAsync().WaitAsync(Timeout);
		Assert.True(microphone.DisposedOnce);
	}

	[Fact]
	public async Task 打开期间停止取消开始且返回空WAV()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []) {BlockOpen = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		Task starting = recorder.StartAsync();
		await microphone.OpenEntered.Task.WaitAsync(Timeout);
		Task<RecordedAudio> stopping = recorder.StopAsync();
		Assert.False(recorder.IsRecording);
		Assert.False(stopping.IsCompleted);
		Assert.Equal(0, microphone.DisposeCount);
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());

		microphone.ReleaseOpen();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Timeout));
		RecordedAudio recorded = await stopping.WaitAsync(Timeout);
		Assert.Equal(44, recorded.Bytes.Length);
		Assert.True(WaveDecoder.IsWave(recorded.Bytes));
		Assert.Equal(0, microphone.ReadCount);
		Assert.True(microphone.DisposedOnce);
	}

	[Fact]
	public async Task 创建期间停止会释放迟到设备且不会打开()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []);
		TaskCompletionSource creating = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim release = new(false);
		using NativeMicrophoneRecorder recorder = new(() =>
		{
			creating.TrySetResult();
			Assert.True(release.Wait(Timeout), "未放行麦克风创建");
			return microphone;
		});

		Task starting = recorder.StartAsync();
		await creating.Task.WaitAsync(Timeout);
		Task<RecordedAudio> stopping = recorder.StopAsync();
		release.Set();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Timeout));
		Assert.Equal(44, (await stopping.WaitAsync(Timeout)).Bytes.Length);
		Assert.False(microphone.Opened);
		Assert.True(microphone.DisposedOnce);
	}

	[Fact]
	public async Task 打开失败释放设备并允许立即重试()
	{
		FakeMicrophone failed = new(new AudioFormat(48000, 1), []) {FailOpen = true};
		FakeMicrophone retry = new(new AudioFormat(16000, 1), []);
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() => Interlocked.Increment(ref created) == 1 ? failed : retry);

		await Assert.ThrowsAsync<AudioDeviceException>(() => recorder.StartAsync().WaitAsync(Timeout));
		Assert.False(recorder.IsRecording);
		Assert.True(failed.DisposedOnce);
		await recorder.StartAsync().WaitAsync(Timeout);
		Assert.Equal(44, (await recorder.StopAsync().WaitAsync(Timeout)).Bytes.Length);
		Assert.True(retry.DisposedOnce);
	}

	[Fact]
	public async Task 已取消的开始不会创建设备()
	{
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() =>
		{
			Interlocked.Increment(ref created);
			return new FakeMicrophone(new AudioFormat(16000, 1), []);
		});
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.StartAsync(cancellation.Token));
		Assert.Equal(0, created);
		Assert.False(recorder.IsRecording);
	}

	[Fact]
	public async Task 打开期间释放不会提前销毁设备或启动读取()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []) {BlockOpen = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		Task starting = recorder.StartAsync();
		await microphone.OpenEntered.Task.WaitAsync(Timeout);
		recorder.Dispose();
		recorder.Dispose();
		Assert.False(recorder.IsRecording);
		Assert.Equal(0, microphone.DisposeCount);
		microphone.ReleaseOpen();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Timeout));
		Assert.True(microphone.DisposedOnce);
		Assert.Equal(0, microphone.ReadCount);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => recorder.StartAsync());
		await Assert.ThrowsAsync<ObjectDisposedException>(() => recorder.StopAsync());
	}

	[Fact]
	public async Task 释放先取消读取再由采集任务唯一释放设备()
	{
		FakeMicrophone microphone = new(new AudioFormat(16000, 1), [])
			{BlockWhenDrained = true, HoldReadAfterStop = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);
		await recorder.StartAsync().WaitAsync(Timeout);
		await microphone.ReadBlocked.Task.WaitAsync(Timeout);

		recorder.Dispose();
		recorder.Dispose();
		Assert.True(microphone.ReadCancelled.Task.IsCompletedSuccessfully);
		Assert.Equal(0, microphone.DisposeCount);
		Assert.False(recorder.IsRecording);
		microphone.ReleaseRead();
		await microphone.Disposed.Task.WaitAsync(Timeout);
		Assert.True(microphone.DisposedOnce);
		Assert.False(microphone.DisposedDuringRead);
	}

	[Fact]
	public async Task 停止等待读取收尾期间不能开始下一段()
	{
		FakeMicrophone microphone = new(new AudioFormat(16000, 1), [0.25f])
			{BlockWhenDrained = true, HoldReadAfterStop = true};
		FakeMicrophone next = new(new AudioFormat(16000, 1), []);
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() => Interlocked.Increment(ref created) == 1 ? microphone : next);
		await recorder.StartAsync().WaitAsync(Timeout);
		await microphone.ReadBlocked.Task.WaitAsync(Timeout);

		Task<RecordedAudio> stopping = recorder.StopAsync();
		Assert.False(stopping.IsCompleted);
		Assert.Equal(0, microphone.DisposeCount);
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		microphone.ReleaseRead();
		Assert.Equal(46, (await stopping.WaitAsync(Timeout)).Bytes.Length);
		Assert.False(microphone.DisposedDuringRead);
		await recorder.StartAsync().WaitAsync(Timeout);
		await recorder.StopAsync().WaitAsync(Timeout);
		Assert.Equal(2, created);
		Assert.True(next.DisposedOnce);
	}

	[Fact]
	public async Task 取消停止等待后后台仍收尾并恢复空闲()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []) {BlockOpen = true};
		FakeMicrophone next = new(new AudioFormat(16000, 1), []);
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() => Interlocked.Increment(ref created) == 1 ? microphone : next);
		using CancellationTokenSource cancellation = new();
		Task starting = recorder.StartAsync();
		await microphone.OpenEntered.Task.WaitAsync(Timeout);
		Task<RecordedAudio> stopping = recorder.StopAsync(cancellation.Token);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(Timeout));
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());

		microphone.ReleaseOpen();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Timeout));
		Assert.True(microphone.DisposedOnce);
		await recorder.StartAsync().WaitAsync(Timeout);
		await recorder.StopAsync().WaitAsync(Timeout);
		Assert.True(next.DisposedOnce);
	}

	[Fact]
	public async Task 外部取消保留已采样本直到停止提取()
	{
		FakeMicrophone microphone = new(new AudioFormat(16000, 1), [0.25f, -0.25f]) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);
		using CancellationTokenSource cancellation = new();
		await recorder.StartAsync(cancellation.Token).WaitAsync(Timeout);
		await microphone.ReadBlocked.Task.WaitAsync(Timeout);

		cancellation.Cancel();
		await microphone.Disposed.Task.WaitAsync(Timeout);
		Assert.True(recorder.IsRecording);
		RecordedAudio recorded = await recorder.StopAsync().WaitAsync(Timeout);
		Assert.Equal(48, recorded.Bytes.Length);
		Assert.Equal(2, WaveDecoder.Decode(recorded.Bytes).Samples.Length);
		Assert.False(microphone.DisposedDuringRead);
	}

	[Fact]
	public async Task 自然结束保留录音会话直到结果被提取()
	{
		FakeMicrophone microphone = new(new AudioFormat(16000, 1), [0.5f]);
		using NativeMicrophoneRecorder recorder = new(() => microphone);
		await recorder.StartAsync().WaitAsync(Timeout);
		await microphone.Disposed.Task.WaitAsync(Timeout);

		Assert.True(recorder.IsRecording);
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		Assert.Equal(46, (await recorder.StopAsync().WaitAsync(Timeout)).Bytes.Length);
	}

	[Fact]
	public async Task 读取失败在停止时报告且下一段仍可录制()
	{
		FakeMicrophone failed = new(new AudioFormat(16000, 1), []) {FailRead = true};
		FakeMicrophone next = new(new AudioFormat(16000, 1), []);
		int created = 0;
		using NativeMicrophoneRecorder recorder = new(() => Interlocked.Increment(ref created) == 1 ? failed : next);
		await recorder.StartAsync().WaitAsync(Timeout);
		await failed.Disposed.Task.WaitAsync(Timeout);

		Assert.True(recorder.IsRecording);
		await Assert.ThrowsAsync<AudioDeviceException>(() => recorder.StopAsync().WaitAsync(Timeout));
		await recorder.StartAsync().WaitAsync(Timeout);
		Assert.Equal(44, (await recorder.StopAsync().WaitAsync(Timeout)).Bytes.Length);
		Assert.True(failed.DisposedOnce);
		Assert.True(next.DisposedOnce);
	}

	// ── 下混与重采样 ───────────────────────────────────────────────────────

	[Fact]
	public void 立体声下混成单声道取两边平均()
	{
		PcmAudio stereo = new()
		{
			Samples = [1f, 0f, 0.5f, -0.5f],      // 两帧：(1,0) 与 (0.5,−0.5)
			SampleRate = 16000,
			Channels = 2,
		};

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(stereo, 16000);

		Assert.Equal(1, mono.Channels);
		Assert.Equal(2, mono.Samples.Length);
		Assert.Equal(0.5f, mono.Samples[0], 3);
		Assert.Equal(0f, mono.Samples[1], 3);
	}

	/// <summary>
	/// 降采样用**区间平均**不用最近邻。
	///
	/// 48000 → 16000 是三取一：最近邻等于直接丢掉三分之二的样本，高频会折回来变成
	/// 嘶声。这条用一段每三个样本里只有一个非零的信号来钉住 —— 最近邻会时而全丢
	/// 时而全取，平均则处处得到同一个值。
	/// </summary>
	[Fact]
	public void 降采样是区间平均而不是丢样本()
	{
		float[] spikes = new float[48];
		for (int index = 0; index < spikes.Length; index += 3) spikes[index] = 0.9f;

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(
			new PcmAudio {Samples = spikes, SampleRate = 48000, Channels = 1}, 16000);

		Assert.Equal(16, mono.Samples.Length);
		// 每一窗覆盖三个源样本，其中恰有一个是 0.9 → 平均 0.3。
		Assert.All(mono.Samples, sample => Assert.Equal(0.3f, sample, 2));
	}

	[Fact]
	public void 采样率一致时长度不变()
	{
		PcmAudio source = new() {Samples = [0.1f, 0.2f, 0.3f], SampleRate = 16000, Channels = 1};

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(source, 16000);

		Assert.Equal(3, mono.Samples.Length);
		Assert.Equal(0.2f, mono.Samples[1], 3);
	}

	[Fact]
	public void 空输入下混之后仍是合法形状()
	{
		PcmAudio empty = NativeMicrophoneRecorder.Downmix(
			new PcmAudio {Samples = [], SampleRate = 48000, Channels = 2}, 16000);

		Assert.Empty(empty.Samples);
		Assert.Equal(1, empty.Channels);
		Assert.Equal(16000, empty.SampleRate);
	}
}
