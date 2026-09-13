using Avalonia.Threading;

namespace Nori.Desktop.Memory;

/// <summary>独立字段防抖与串行保存；失败时保留草稿，旧响应不得清除新的修改。</summary>
internal sealed class MemorySettingDraft
{
	private readonly Func<object, Task> _save;
	private readonly Action _changed;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private CancellationTokenSource? _delay;
	private long _revision;

	public MemorySettingDraft(object value, Func<object, Task> save, Action changed)
	{
		Value = value;
		_save = save;
		_changed = changed;
	}

	public object Value { get; private set; }
	public bool Dirty { get; private set; }
	public bool Saving { get; private set; }
	public string Error { get; private set; } = "";

	public void AcceptSnapshot(object value)
	{
		if (!Dirty && !Saving) Value = value;
	}

	public void Set(object value)
	{
		Value = value;
		Dirty = true;
		Error = "";
		_revision++;
		CancelDelay();
		_delay = new CancellationTokenSource();
		_ = DebounceAsync(_delay.Token);
		_changed();
	}

	private async Task DebounceAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(400, cancellationToken);
			await Dispatcher.UIThread.InvokeAsync(async () => await FlushAsync());
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
	}

	public async Task<bool> FlushAsync()
	{
		CancelDelay();
		await _gate.WaitAsync();
		try
		{
			while (Dirty)
			{
				long revision = _revision;
				object value = Value;
				Saving = true;
				_changed();
				try
				{
					await _save(value);
					if (revision == _revision) { Dirty = false; Error = ""; }
				}
				catch (Exception ex)
				{
					Error = ex.Message;
					return false;
				}
				finally { Saving = false; _changed(); }
			}
			return true;
		}
		finally { _gate.Release(); }
	}

	private void CancelDelay()
	{
		CancellationTokenSource? delay = _delay;
		_delay = null;
		delay?.Cancel();
		delay?.Dispose();
	}
}
