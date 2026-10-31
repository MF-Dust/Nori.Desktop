using System.Threading.Channels;
using Nori.Core.Notifications;
using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>串行处理通知 IPC；Hide 立即失效点击，迟到的 Notify 回包仍会被撤销。</summary>
internal sealed class LinuxNativeNotifier : INativeNotifier, IDisposable, IAsyncDisposable
{
	private sealed class Entry(ApprovalNotice notice)
	{
		internal ApprovalNotice Notice { get; } = notice;
		internal ILinuxApprovalNotification? Client { get; set; }
		internal uint Id { get; set; }
	}

	private readonly object _gate = new();
	private readonly Dictionary<string, Entry> _active = [];
	private readonly HashSet<Entry> _sent = [];
	private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Func<ILinuxApprovalNotification> _create;
	private readonly Func<bool> _english;
	private readonly Action<string> _activated;
	private readonly Action<Exception> _failure;
	private readonly Task _worker;
	private bool _disposed;

	internal LinuxNativeNotifier(Func<bool> english, Action<string> activated, Action<Exception> failure)
		: this(english, activated, failure, () => new LinuxApprovalNotification()) { }

	internal LinuxNativeNotifier(Func<bool> english, Action<string> activated, Action<Exception> failure, Func<ILinuxApprovalNotification> create)
	{
		_english = english;
		_activated = activated;
		_failure = failure;
		_create = create;
		_worker = Task.Run(RunAsync);
	}

	public bool Available { get { lock (_gate) return !_disposed && !string.IsNullOrWhiteSpace(DBusAddress.Session); } }

	public void Show(ApprovalNotice notice)
	{
		lock (_gate)
		{
			if (_disposed || _active.ContainsKey(notice.RequestId)) return;
			Entry entry = new(notice);
			_active.Add(notice.RequestId, entry);
			_queue.Writer.TryWrite(() => SendAsync(entry));
		}
	}

	public void Hide(string requestId)
	{
		lock (_gate)
		{
			if (_active.Remove(requestId, out Entry? entry)) _queue.Writer.TryWrite(() => CloseAsync(entry));
		}
	}

	private bool IsActive(Entry entry)
	{
		lock (_gate) return !_disposed && _active.TryGetValue(entry.Notice.RequestId, out Entry? current) && ReferenceEquals(current, entry);
	}

	private async Task SendAsync(Entry entry)
	{
		if (!IsActive(entry)) return;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
		timeout.CancelAfter(TimeSpan.FromSeconds(3));
		try
		{
			entry.Client = _create();
			_sent.Add(entry);
			entry.Id = await entry.Client.SendAsync(entry.Notice, _english(),
				signal => _queue.Writer.TryWrite(() => SignalAsync(entry, signal)), timeout.Token).ConfigureAwait(false);
			if (!IsActive(entry)) await CloseAsync(entry).ConfigureAwait(false);
		}
		catch
		{
			Remove(entry);
			entry.Client?.Dispose();
			_sent.Remove(entry);
			throw;
		}
	}

	private async Task SignalAsync(Entry entry, LinuxNotificationSignal signal)
	{
		if (!IsActive(entry) || entry.Id != signal.Id) return;
		if (signal.Action is null)
		{
			// 用户关闭系统通知不等于拒绝授权，应用内卡片继续等待。
			Remove(entry);
			_sent.Remove(entry);
			entry.Client?.Dispose();
			return;
		}
		if (entry.Client?.SupportsActions != true) return;
		string arguments = signal.Action == "default" ? ToastActivation.Open(entry.Notice.RequestId) : signal.Action;
		if (signal.Action != "default"
			&& arguments != ToastActivation.Encode(ToastAction.Allow, entry.Notice.RequestId)
			&& arguments != ToastActivation.Encode(ToastAction.Deny, entry.Notice.RequestId)) return;
		if (signal.Action == "default")
		{
			_activated(arguments);
			return;
		}
		Remove(entry);
		try { _activated(arguments); }
		finally { await CloseAsync(entry).ConfigureAwait(false); }
	}

	private void Remove(Entry entry)
	{
		lock (_gate)
		{
			if (_active.TryGetValue(entry.Notice.RequestId, out Entry? current) && ReferenceEquals(current, entry))
				_active.Remove(entry.Notice.RequestId);
		}
	}

	private async Task CloseAsync(Entry entry)
	{
		if (!_sent.Remove(entry)) return;
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(2));
			if (entry.Id != 0) await entry.Client!.CloseAsync(entry.Id, timeout.Token).ConfigureAwait(false);
		}
		finally { entry.Client?.Dispose(); }
	}

	private async Task RunAsync()
	{
		try
		{
			await foreach (Func<Task> work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
			{
				try { await work().ConfigureAwait(false); }
				catch (Exception failure) { Report(failure); }
			}
		}
		finally
		{
			foreach (Entry entry in _sent.ToArray())
			{
				try { await CloseAsync(entry).ConfigureAwait(false); }
				catch (Exception failure) { Report(failure); }
			}
			_shutdown.Dispose();
		}
	}

	private void Report(Exception failure)
	{
		try { _failure(failure); }
		catch { /* 通知和日志故障都不能影响授权。 */ }
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_active.Clear();
			_shutdown.CancelAfter(TimeSpan.FromSeconds(4));
			_queue.Writer.TryComplete();
		}
	}

	public async ValueTask DisposeAsync()
	{
		Dispose();
		await _worker.ConfigureAwait(false);
	}
}
