namespace Nori.Core.Voice.Audio;

/// <summary>
/// 串行化采集会话的打开与释放。调用方须在后台调用；释放回调必须先停止读取并释放旧句柄，
/// 且能处理部分打开或尚未打开的设备。回调不应重入本策略。
/// </summary>
public sealed class AudioDeviceRecoveryPolicy(Action openCapture, Action releaseCapture, string? deviceId = null)
{
	private const int MaxBusyRetries = 2;
	private readonly Lock _gate = new();
	private bool _userStarted;
	private bool _userStopped;
	private bool _networkAvailable = true;
	private int _busyFailures;
	private long _retryAfter;
	private bool _captureOpen;
	private bool _opening;
	private int _generation;
	private readonly string? _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

	public Task MarkUserStartedAsync()
	{
		lock (_gate)
		{
			InvalidateCapture();
			_userStarted = true;
			_userStopped = false;
			_busyFailures = 0;
			_retryAfter = 0;
			return TryOpenAsync();
		}
	}

	public void MarkUserStopped()
	{
		lock (_gate)
		{
			_userStopped = true;
			InvalidateCapture();
		}
	}

	public Task SetNetworkAvailableAsync(bool available)
	{
		lock (_gate)
		{
			if (_networkAvailable == available) return Task.CompletedTask;
			_networkAvailable = available;
			if (!available) InvalidateCapture();
			return TryOpenAsync();
		}
	}

	public Task HandleDeviceChangeAsync(AudioDeviceChangedEventArgs change)
	{
		lock (_gate)
		{
			if (change.Direction == AudioDeviceDirection.Output) return Task.CompletedTask;
			if (change.Kind == AudioDeviceChangeKind.DefaultChanged && _deviceId is not null)
				return Task.CompletedTask;
			if (_deviceId is null && change.IsDefault == false) return Task.CompletedTask;
			if (_deviceId is not null && change.DeviceId is not null && change.DeviceId != _deviceId)
				return Task.CompletedTask;
			switch (change.Kind)
			{
				case AudioDeviceChangeKind.Removed:
				case AudioDeviceChangeKind.Disconnected:
					InvalidateCapture();
					return Task.CompletedTask;
				case AudioDeviceChangeKind.DefaultChanged:
					InvalidateCapture();
					if (change.DeviceId is null) return Task.CompletedTask;
					break;
				case AudioDeviceChangeKind.FormatChanged:
				case AudioDeviceChangeKind.SystemWake:
					InvalidateCapture();
					break;
			}
			return TryOpenAsync();
		}
	}

	private void InvalidateCapture()
	{
		_generation++;
		_opening = false;
		if (!_captureOpen) return;
		releaseCapture();
		_captureOpen = false;
	}

	private async Task TryOpenAsync()
	{
		if (!_userStarted || _userStopped || !_networkAvailable || _busyFailures > MaxBusyRetries || _captureOpen || _opening)
			return;
		_opening = true;
		int generation = _generation;
		while (true)
		{
			long wait;
			lock (_gate)
			{
				if (generation != _generation) return;
				wait = _retryAfter - Environment.TickCount64;
				if (wait <= 0)
				{
					try
					{
						openCapture();
						_captureOpen = true;
						_opening = false;
						_busyFailures = 0;
						_retryAfter = 0;
						return;
					}
					catch (Exception error)
					{
						_opening = false;
						releaseCapture();
						if (error is not AudioDeviceException { IsBusy: true }) throw;
						if (++_busyFailures > MaxBusyRetries)
						{
							throw new AudioDeviceException("麦克风被占用，重试后仍无法打开。请关闭占用麦克风的应用后手动重试。", error)
							{
								IsBusy = true,
							};
						}
						_opening = true;
						wait = 100L << (_busyFailures - 1);
						_retryAfter = Environment.TickCount64 + wait;
					}
				}
			}
			await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
		}
	}
}
