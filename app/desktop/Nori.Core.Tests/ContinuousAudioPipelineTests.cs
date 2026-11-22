using System.Buffers.Binary;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Realtime;

namespace Nori.Core.Tests;

/// <summary>C1 连续音频管线与 C2 有界音频队列单元测试。</summary>
public sealed class ContinuousAudioPipelineTests
{
	[Theory]
	[InlineData(16000, 1, 24000, 137)]
	[InlineData(44100, 1, 24000, 137)]
	[InlineData(44100, 2, 24000, 512)]
	[InlineData(48000, 1, 24000, 1024)]
	[InlineData(48000, 2, 24000, 2048)]
	[InlineData(48000, 2, 16000, 137)]
	[InlineData(44100, 2, 16000, 512)]
	[InlineData(24000, 1, 16000, 333)]
	[InlineData(16000, 2, 16000, 257)]
	public void 正弦波任意分块重采样与整块一致且跨块边界无跳变(
		int sourceRate,
		int channels,
		int targetRate,
		int chunkSamples)
	{
		const double frequency = 440.0;
		const int durationSeconds = 1;
		int totalFrames = sourceRate * durationSeconds;
		float[] interleaved = CreateStereoOrMultiChannelSine(totalFrames, channels, sourceRate, frequency, 0.5f);

		ContinuousAudioProcessor wholeProcessor = new(sourceRate, channels, targetRate);
		AudioProcessResult wholeHead = wholeProcessor.Process(interleaved);
		AudioProcessResult wholeTail = wholeProcessor.Flush();
		float[] wholeOutput = [.. wholeHead.MonoResampled, .. wholeTail.MonoResampled];

		ContinuousAudioProcessor chunkProcessor = new(sourceRate, channels, targetRate);
		List<float> chunkedOutput = new(wholeOutput.Length + 8);
		List<short> chunkedPcm16 = new(wholeOutput.Length + 8);
		List<int> boundaryIndices = [];

		for (int offset = 0; offset < interleaved.Length; offset += chunkSamples)
		{
			int count = Math.Min(chunkSamples, interleaved.Length - offset);
			AudioProcessResult part = chunkProcessor.Process(interleaved.AsSpan(offset, count));
			if (part.OutputFrames > 0)
			{
				if (chunkedOutput.Count > 0) boundaryIndices.Add(chunkedOutput.Count);
				chunkedOutput.AddRange(part.MonoResampled);
				for (int index = 0; index < part.Pcm16Bytes.Length; index += 2)
					chunkedPcm16.Add(BinaryPrimitives.ReadInt16LittleEndian(part.Pcm16Bytes.AsSpan(index, 2)));
			}
		}

		AudioProcessResult tail = chunkProcessor.Flush();
		chunkedOutput.AddRange(tail.MonoResampled);
		for (int index = 0; index < tail.Pcm16Bytes.Length; index += 2)
			chunkedPcm16.Add(BinaryPrimitives.ReadInt16LittleEndian(tail.Pcm16Bytes.AsSpan(index, 2)));

		Assert.Equal(wholeOutput.Length, chunkedOutput.Count);
		Assert.InRange(chunkedOutput.Count, targetRate - 1, targetRate + 1);
		Assert.Equal(chunkedOutput.Count, chunkedPcm16.Count);

		double maxDiff = 0;
		for (int index = 0; index < wholeOutput.Length; index++)
		{
			double diff = Math.Abs(wholeOutput[index] - chunkedOutput[index]);
			if (diff > maxDiff) maxDiff = diff;
		}
		Assert.InRange(maxDiff, 0, 1e-6);

		// 检查每个跨块拼接点的一阶差分连续性（440Hz 正弦波每样本步进远小于 0.15）。
		double maxStepPerSample = (2.0 * Math.PI * frequency / targetRate) * 0.5 * 1.5;
		foreach (int boundary in boundaryIndices)
		{
			double jump = Math.Abs(chunkedOutput[boundary] - chunkedOutput[boundary - 1]);
			Assert.InRange(jump, 0, maxStepPerSample);
		}
	}

	[Fact]
	public void 奇异非整帧交错块跨块保留残余采样不丢帧且多声道均衡下混()
	{
		// 4 声道，每次喂 3、2、7 个样本（均非 4 的倍数），总共 12 个样本 = 3 帧。
		ContinuousAudioProcessor processor = new(16000, 4, 16000);

		AudioProcessResult first = processor.Process([0.2f, 0.4f, 0.6f]);
		Assert.Equal(0, first.InputFramesProcessed);
		Assert.Empty(first.MonoResampled);

		AudioProcessResult second = processor.Process([0.8f, 1.0f]);
		Assert.Equal(1, second.InputFramesProcessed);
		Assert.Single(second.MonoResampled);
		Assert.Equal(0.5f, second.MonoResampled[0], 5);

		AudioProcessResult third = processor.Process([0.0f, 0.0f, 0.0f, 0.4f, -0.4f, 0.8f, 0.8f]);
		Assert.Equal(2, third.InputFramesProcessed);
		Assert.Equal(2, third.MonoResampled.Length);
		// 第 2 帧：[1.0, 0.0, 0.0, 0.0] -> 0.25
		Assert.Equal(0.25f, third.MonoResampled[0], 5);
		// 第 3 帧：[0.4, -0.4, 0.8, 0.8] -> 0.40
		Assert.Equal(0.40f, third.MonoResampled[1], 5);
	}

	[Fact]
	public void 长时间分块重采样不产生累计时长漂移()
	{
		const int sourceRate = 44100;
		const int targetRate = 24000;
		const int seconds = 20;
		StatefulLinearResampler resampler = new(sourceRate, targetRate);

		float[] chunk = new float[137];
		int remaining = sourceRate * seconds;
		long emitted = 0;
		while (remaining > 0)
		{
			int take = Math.Min(chunk.Length, remaining);
			emitted += resampler.Process(chunk.AsSpan(0, take)).Length;
			remaining -= take;
		}
		emitted += resampler.Flush().Length;

		Assert.Equal(sourceRate * seconds, resampler.TotalInputFrames);
		Assert.Equal(targetRate * seconds, emitted);
		Assert.Equal(emitted, resampler.TotalOutputFrames);
	}

	[Fact]
	public void 非有限值和超幅样本被安全夹紧且不污染后续样本()
	{
		ContinuousAudioProcessor processor = new(48000, 1, 24000);
		float[] dirty = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 3.5f, -4.2f, 0.5f, -0.5f, 0.0f];

		AudioProcessResult result = processor.Process(dirty);
		short[] pcm16Samples = new ContinuousAudioProcessor(48000, 1, 24000).ProcessToPcm16Samples(dirty);

		Assert.Equal(result.OutputFrames, pcm16Samples.Length);
		Assert.All(result.MonoResampled, sample => Assert.True(float.IsFinite(sample)));
		Assert.Equal(0, pcm16Samples[0]); // NaN -> 0
		Assert.Equal(0, pcm16Samples[1]); // -Infinity -> 0 (stride 2 from 48k -> 24k)
		// 48k -> 24k 取偶数帧 [0]=NaN(0), [2]=-Inf(0), [4]=-4.2f(-32767), [6]=-0.5f(-16384)
		Assert.Equal(-short.MaxValue, pcm16Samples[2]);
		Assert.Equal((short)Math.Round(-0.5f * short.MaxValue), pcm16Samples[3]);

		Assert.Equal(short.MaxValue, ContinuousAudioProcessor.FloatToInt16(2.5f));
		Assert.Equal(-short.MaxValue, ContinuousAudioProcessor.FloatToInt16(-2.5f));
		Assert.Equal(0, ContinuousAudioProcessor.FloatToInt16(float.NaN));
		Assert.Equal(0, ContinuousAudioProcessor.FloatToInt16(float.PositiveInfinity));
		Assert.Equal(0, ContinuousAudioProcessor.FloatToInt16(float.NegativeInfinity));
	}

	[Fact]
	public void 输入电平在发声时大于零且静音衰减或重置后归零()
	{
		ContinuousAudioProcessor processor = new(16000, 1, 24000);
		Assert.Equal(0, processor.InputLevel);

		float[] silence = new float[320];
		AudioProcessResult quiet = processor.Process(silence);
		Assert.Equal(0, quiet.InputLevel);

		float[] loud = new float[320];
		for (int index = 0; index < loud.Length; index++)
			loud[index] = 0.5f * MathF.Sin(index * 0.15f);
		AudioProcessResult active = processor.Process(loud);
		Assert.InRange(active.InputLevel, 0.1, 1.0);

		for (int round = 0; round < 60; round++)
			processor.Process(silence);
		Assert.Equal(0, processor.InputLevel);

		processor.Process(loud);
		Assert.True(processor.InputLevel > 0);
		processor.Reset();
		Assert.Equal(0, processor.InputLevel);
	}

	[Fact]
	public void BoundedAudioQueue溢出时保持字节上限并立即清空和抹除内存()
	{
		using BoundedAudioQueue queue = new(maxBufferedBytes: 1024, maxChunks: 64, maxDurationMilliseconds: 30_000);
		float[] chunk = Enumerable.Repeat(0.75f, 128).ToArray(); // 512 bytes

		Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
		Assert.Equal(512, queue.CurrentBufferedBytes);
		Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
		Assert.Equal(1024, queue.CurrentBufferedBytes);
		Assert.Equal(1024, queue.PeakBufferedBytes);
		Assert.False(queue.OverflowToken.IsCancellationRequested);

		// 第三块将超过 1024 字节上限，立即触发溢出并清空队列。
		Assert.Equal(AudioEnqueueResult.Overflowed, queue.TryEnqueue(chunk, 16000, 1));
		Assert.True(queue.IsOverflowed);
		Assert.True(queue.OverflowToken.IsCancellationRequested);
		Assert.Equal(0, queue.CurrentBufferedBytes);
		Assert.Equal(1024, queue.PeakBufferedBytes);
		Assert.True(queue.PeakBufferedBytes <= queue.MaxBufferedBytes);
	}

	[Fact]
	public void BoundedAudioQueue限制单轮总时长不超过上限()
	{
		using BoundedAudioQueue queue = new(maxBufferedBytes: 4 * 1024 * 1024, maxChunks: 256, maxDurationMilliseconds: 100);
		float[] chunk = new float[800]; // 50ms @ 16kHz mono

		Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
		Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
		Assert.Equal(100, queue.TotalEnqueuedMilliseconds, 2);

		Assert.Equal(AudioEnqueueResult.DurationLimitReached, queue.TryEnqueue(chunk, 16000, 1));
		Assert.True(queue.IsDurationLimitReached);
	}

	[Theory]
	[InlineData(16000, 24000)]
	[InlineData(44100, 24000)]
	[InlineData(48000, 16000)]
	public void 极值端点重采样使用double中间精度避免减法溢出(int sourceRate, int targetRate)
	{
		StatefulLinearResampler resampler = new(sourceRate, targetRate);
		float[] extremes = [float.MaxValue, -float.MaxValue, float.MaxValue, -float.MaxValue];

		float[] first = resampler.Process(extremes.AsSpan(0, 2));
		float[] second = resampler.Process(extremes.AsSpan(2, 2));
		float[] tail = resampler.Flush();

		float[] all = [.. first, .. second, .. tail];
		Assert.NotEmpty(all);
		Assert.All(all, sample => Assert.True(float.IsFinite(sample), $"发现非有限重采样结果: {sample}"));
	}

	[Fact]
	public async Task BoundedAudioQueue在MaxChunks为1的紧耦合交错读写下不误判溢出()
	{
		using BoundedAudioQueue queue = new(maxBufferedBytes: 64 * 1024, maxChunks: 1, maxDurationMilliseconds: 30_000);
		float[] chunk = [0.25f, -0.25f, 0.5f, -0.5f];

		// 同步紧耦合交错：每次入队后立即出队，绝不因竞态窗口误判已满。
		for (int round = 0; round < 200; round++)
		{
			Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
			Assert.True(queue.TryDequeue(out float[]? dequeued));
			Assert.NotNull(dequeued);
			Array.Clear(dequeued);
			Assert.Equal(0, queue.CurrentBufferedBytes);
		}
		Assert.False(queue.IsOverflowed);

		// 异步生产者与消费者严格交替（等待消费者取走后再写入下一块）验证 ReadAllAsync 原子扣减配额。
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		int consumed = 0;
		TaskCompletionSource? slotReady = null;
		Task consumer = Task.Run(async () =>
		{
			await foreach (float[] item in queue.ReadAllAsync(cts.Token))
			{
				Array.Clear(item);
				Interlocked.Increment(ref consumed);
				Volatile.Read(ref slotReady)?.TrySetResult();
			}
		});

		for (int round = 0; round < 50; round++)
		{
			TaskCompletionSource taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Volatile.Write(ref slotReady, taken);
			Assert.Equal(AudioEnqueueResult.Enqueued, queue.TryEnqueue(chunk, 16000, 1));
			await taken.Task.WaitAsync(TimeSpan.FromSeconds(2));
		}
		queue.Complete();
		await consumer.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(50, consumed);
		Assert.False(queue.IsOverflowed);
	}

	private static float[] CreateStereoOrMultiChannelSine(int frames, int channels, int sampleRate, double frequency, float amplitude)
	{
		float[] samples = new float[frames * channels];
		for (int frame = 0; frame < frames; frame++)
		{
			float value = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * frame / sampleRate));
			for (int ch = 0; ch < channels; ch++)
				samples[(frame * channels) + ch] = value;
		}
		return samples;
	}
}
