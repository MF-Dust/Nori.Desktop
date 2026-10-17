using Nori.Core.Voice;

namespace Nori.Core.Tests;

/// <summary>语音流水线联接器: 主错误保留, 次要取消噪音不聚合。</summary>
public sealed class VoicePipelineTests
{
	[Fact]
	public async Task 生产者失败后消费者取消仍保留原始异常()
	{
		HttpRequestException primary = new("Provider 失败");
		using CancellationTokenSource consumerCts = new();
		TaskCompletionSource producerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource consumerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using CancellationTokenRegistration registration = consumerCts.Token.Register(
			() => consumerCompletion.TrySetCanceled(consumerCts.Token));
		Task join = VoicePipeline.JoinAsync(producerCompletion.Task, consumerCompletion.Task);

		producerCompletion.SetException(primary);
		Exception producerError = await Assert.ThrowsAsync<HttpRequestException>(() => producerCompletion.Task);
		Assert.Same(primary, producerError);
		await consumerCts.CancelAsync();

		Exception error = await Assert.ThrowsAsync<HttpRequestException>(() => join);
		Assert.Same(primary, error);
	}

	[Fact]
	public async Task 消费者失败后生产者取消仍保留原始异常()
	{
		InvalidOperationException primary = new("播放失败");
		using CancellationTokenSource producerCts = new();
		TaskCompletionSource producerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource consumerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using CancellationTokenRegistration registration = producerCts.Token.Register(
			() => producerCompletion.TrySetCanceled(producerCts.Token));
		Task join = VoicePipeline.JoinAsync(producerCompletion.Task, consumerCompletion.Task);

		consumerCompletion.SetException(primary);
		Exception consumerError = await Assert.ThrowsAsync<InvalidOperationException>(() => consumerCompletion.Task);
		Assert.Same(primary, consumerError);
		await producerCts.CancelAsync();

		Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => join);
		Assert.Same(primary, error);
	}

	[Fact]
	public async Task 生产者先取消时仍观察并保留消费者失败()
	{
		IOException primary = new("播放失败");
		Task producer = Task.FromCanceled(new CancellationToken(true));
		Task consumer = Task.FromException(primary);

		IOException error = await Assert.ThrowsAsync<IOException>(() => VoicePipeline.JoinAsync(producer, consumer));

		Assert.Same(primary, error);
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
