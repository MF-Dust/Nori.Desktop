using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Nori.Core.Voice.Stt;

internal enum EventWriteResult
{
	Written,
	TimedOut,
	Completed,
}

/// <summary>
/// 有界流式识别事件通道。
///
/// 消费者正常读取时按有界容量提供背压等待，防止慢消费导致内存无限增长；
/// 当需要投递 <c>Completed</c> / <c>Failed</c> / <c>Error</c> / <c>Closed</c> 等终态事件时，
/// 若通道已满则优先淘汰最旧的非终态事件（如增量片段），保证终态事件绝不静默丢失。
/// </summary>
internal sealed class BoundedSttEventChannel : ChannelReader<StreamingSttEvent>
{
	private readonly Lock _gate = new();
	private readonly LinkedList<StreamingSttEvent> _queue = new();
	private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _writerCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly int _capacity;

	private TaskCompletionSource<bool> _readWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private TaskCompletionSource _spaceWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _completed;

	public BoundedSttEventChannel(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		_capacity = capacity;
	}

	public override bool CanCount => true;

	public override bool CanPeek => true;

	public override int Count
	{
		get
		{
			lock (_gate) return _queue.Count;
		}
	}

	public override Task Completion => _completionTcs.Task;

	internal Task WriterCompleted => _writerCompletedTcs.Task;

	/// <summary>按有界等待方式写入事件；区分成功、背压超时与通道已完结。</summary>
	public async ValueTask<EventWriteResult> WriteWithWaitAsync(
		StreamingSttEvent item,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		long deadlineTicks = Environment.TickCount64 + Math.Max(1L, (long)timeout.TotalMilliseconds);

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			Task spaceTask;
			TaskCompletionSource<bool>? readWaiterToSignal = null;

			lock (_gate)
			{
				if (_completed) return EventWriteResult.Completed;
				if (_queue.Count < _capacity)
				{
					_queue.AddLast(item);
					readWaiterToSignal = SwapReadWaiterLocked();
				}
				else
				{
					spaceTask = _spaceWaiter.Task;
					goto WaitForSpace;
				}
			}

			readWaiterToSignal?.TrySetResult(true);
			return EventWriteResult.Written;

		WaitForSpace:
			long remainingMs = deadlineTicks - Environment.TickCount64;
			if (remainingMs <= 0) return EventWriteResult.TimedOut;

			try
			{
				await spaceTask.WaitAsync(TimeSpan.FromMilliseconds(remainingMs), cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				return EventWriteResult.TimedOut;
			}
		}
	}

	/// <summary>
	/// 强制写入关键终态事件（<c>Completed</c> / <c>Failed</c> / <c>Error</c> / <c>Closed</c>）。
	/// 通道满时优先淘汰最旧的非终态事件以保持容量上限，若队列全部为终态事件则仍保留该终态事件以防静默丢失。
	/// </summary>
	public void ForceEnqueueTerminal(StreamingSttEvent terminalEvent)
	{
		TaskCompletionSource<bool>? readWaiterToSignal = null;

		lock (_gate)
		{
			if (_completed) return;

			if (_queue.Count >= _capacity)
			{
				LinkedListNode<StreamingSttEvent>? node = _queue.First;
				while (node is not null)
				{
					if (!IsTerminalKind(node.Value.Kind))
					{
						_queue.Remove(node);
						break;
					}
					node = node.Next;
				}
			}

			_queue.AddLast(terminalEvent);
			readWaiterToSignal = SwapReadWaiterLocked();
		}

		readWaiterToSignal?.TrySetResult(true);
	}

	public void Complete()
	{
		TaskCompletionSource<bool>? readWaiterToSignal = null;
		TaskCompletionSource? spaceWaiterToSignal = null;
		bool markCompleted = false;

		lock (_gate)
		{
			if (_completed) return;
			_completed = true;
			readWaiterToSignal = SwapReadWaiterLocked();
			spaceWaiterToSignal = SwapSpaceWaiterLocked();
			markCompleted = _queue.Count == 0;
		}

		readWaiterToSignal?.TrySetResult(false);
		spaceWaiterToSignal?.TrySetResult();
		_writerCompletedTcs.TrySetResult();
		if (markCompleted)
		{
			_completionTcs.TrySetResult();
		}
	}

	public override bool TryPeek([MaybeNullWhen(false)] out StreamingSttEvent item)
	{
		lock (_gate)
		{
			if (_queue.First is { } first)
			{
				item = first.Value;
				return true;
			}
			item = null;
			return false;
		}
	}

	public override bool TryRead([MaybeNullWhen(false)] out StreamingSttEvent item)
	{
		TaskCompletionSource? spaceWaiterToSignal = null;
		bool markCompleted = false;

		lock (_gate)
		{
			if (_queue.First is not { } first)
			{
				item = null;
				return false;
			}

			item = first.Value;
			_queue.RemoveFirst();

			if (_queue.Count < _capacity)
			{
				spaceWaiterToSignal = SwapSpaceWaiterLocked();
			}

			markCompleted = _completed && _queue.Count == 0;
		}

		spaceWaiterToSignal?.TrySetResult();
		if (markCompleted)
		{
			_completionTcs.TrySetResult();
		}
		return true;
	}

	public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return ValueTask.FromCanceled<bool>(cancellationToken);
		}

		Task<bool> waitTask;
		lock (_gate)
		{
			if (_queue.Count > 0) return new ValueTask<bool>(true);
			if (_completed) return new ValueTask<bool>(false);
			waitTask = _readWaiter.Task;
		}

		return new ValueTask<bool>(cancellationToken.CanBeCanceled ? waitTask.WaitAsync(cancellationToken) : waitTask);
	}

	public static bool IsTerminalKind(StreamingSttEventKind kind) =>
		kind is StreamingSttEventKind.Completed
			or StreamingSttEventKind.Failed
			or StreamingSttEventKind.Error
			or StreamingSttEventKind.Closed;

	private TaskCompletionSource<bool> SwapReadWaiterLocked()
	{
		TaskCompletionSource<bool> current = _readWaiter;
		_readWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		return current;
	}

	private TaskCompletionSource SwapSpaceWaiterLocked()
	{
		TaskCompletionSource current = _spaceWaiter;
		_spaceWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		return current;
	}
}
