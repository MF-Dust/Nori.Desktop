using Nori.Core.Voice;

namespace Nori.Core.Tests;

/// <summary>语音流水线联接器: 主错误保留, 次要取消噪音不聚合。</summary>
public sealed class VoicePipelineTests
{
	[Fact]
	public async Task 生产者失败时其原始异常是主错误()
	{
		HttpRequestException primary = new("Provider 失败");
		using CancellationTokenSource pipelineCts = new();
		Task producer = Task.Run(async () =>
		{
			await Task.Delay(10);
			throw primary;
		});
		Task consumer = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(50, pipelineCts.Token);
			}
			catch (OperationCanceledException)
			{
				// 生产者失败内部取消消费者 (模拟 VoiceService.ConsumeAsync 的过滤行为)。
			}
		});

		Exception error = await Assert.ThrowsAsync<HttpRequestException>(() =>
			VoicePipeline.JoinAsync(producer, consumer));

		Assert.Same(primary, error);
		await pipelineCts.CancelAsync();
	}

	[Fact]
	public async Task 消费者失败时生产者的内部取消不参与语义()
	{
		InvalidOperationException primary = new("播放失败");
		using CancellationTokenSource pipelineCts = new();
		Task consumer = Task.Run(async () =>
		{
			await Task.Delay(10);
			throw primary;
		});
		Task producer = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(50, pipelineCts.Token);
			}
			catch (OperationCanceledException)
			{
				throw; // 生产者被流水线取消直接抛 OCE (模拟 ThrowIfCancellationRequested)。
			}
		});

		Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			VoicePipeline.JoinAsync(producer, consumer));

		Assert.Same(primary, error);
		await pipelineCts.CancelAsync();
	}

	[Fact]
	public async Task 双方正常完成时不抛出()
	{
		await VoicePipeline.JoinAsync(Task.CompletedTask, Task.CompletedTask);
	}

	[Fact]
	public async Task 用户取消生产者时取消语义保留()
	{
		using CancellationTokenSource cts = new();
		Task producer = Task.Run(async () =>
		{
			await Task.Delay(10, cts.Token);
			return;
		});
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			VoicePipeline.JoinAsync(producer, Task.CompletedTask));
	}
}
