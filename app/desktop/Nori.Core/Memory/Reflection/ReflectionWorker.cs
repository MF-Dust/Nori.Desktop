using System.Threading.Channels;

namespace Nori.Core.Memory;

/// <summary>
/// 单消费者 Reflection 后台 Worker。
/// 容量 1 的丢弃式信号队列：重复触发只保留一次待处理工作。
/// </summary>
public sealed class ReflectionWorker : IAsyncDisposable
{
	private static readonly object Signal = new();
	private readonly ReflectionService _service;
	private readonly Action<Exception>? _onError;
	private readonly Action? _onCompleted;
	private readonly Channel<object> _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(1)
	{
		FullMode = BoundedChannelFullMode.DropWrite,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly CancellationTokenSource _cts = new();
	private Task? _worker;
	private int _started;

	public ReflectionWorker(ReflectionService service, Action<Exception>? onError = null, Action? onCompleted = null)
	{
		_service = service;
		_onError = onError;
		_onCompleted = onCompleted;
	}

	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) != 0) return;
		_worker = Task.Run(RunAsync);
	}

	public bool TryEnqueue() => _channel.Writer.TryWrite(Signal);

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "后台反思任务失败需记录并继续调度。")]
	private async Task RunAsync()
	{
		try
		{
			await foreach (object _ in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
			{
				try
				{
					if (await _service.ReflectPendingAsync(_cts.Token).ConfigureAwait(false))
					{
						try { _onCompleted?.Invoke(); }
						catch { }
						// 处理期间可能又有完整轮次进入队列，下一次循环继续检查持久化游标。
					}
				}
				catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
				catch (Exception exception) { _onError?.Invoke(exception); }
			}
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
		_channel.Writer.TryComplete();
		if (_worker is not null)
		{
			try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (TimeoutException) { }
		}
		_cts.Dispose();
	}
}
