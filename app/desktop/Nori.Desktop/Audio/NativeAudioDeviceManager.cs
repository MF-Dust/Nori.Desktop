using System.Diagnostics;
using System.Runtime.InteropServices;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio.Linux;
using Nori.Desktop.Audio.Mac;
using Nori.Desktop.Audio.Windows;

namespace Nori.Desktop.Audio;

internal sealed class NativeAudioDeviceManager() : IAudioDeviceManager, IAudioCaptureSource
{
	private const int MaxDeviceIdLength = 1024;
	private const string BluetoothNote = "使用蓝牙麦克风时，系统可能从 A2DP 高音质播放切换到 HFP/免提双向模式，音质和声道数可能下降。可改用另一只麦克风以保留高音质播放。";
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
	private readonly Lock _gate = new();
	private Dictionary<AudioDeviceDirection, IReadOnlyList<AudioDeviceInfo>> _snapshots = [];
	private Func<AudioDeviceDirection, IReadOnlyList<AudioDeviceInfo>> _enumerate = EnumeratePlatform;
	private Action<AudioDeviceChangedEventArgs>? _devicesChanged;
	private Timer? _timer;
	private int _polling;
	private int _generation;
	private bool _disposed;

	internal NativeAudioDeviceManager(Func<AudioDeviceDirection, IReadOnlyList<AudioDeviceInfo>> enumerate) : this() =>
		_enumerate = enumerate;

	public event Action<AudioDeviceChangedEventArgs>? DevicesChanged
	{
		add
		{
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_devicesChanged += value;
				if (_devicesChanged is not null)
					_timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
			}
		}
		remove
		{
			lock (_gate)
			{
				_devicesChanged -= value;
				if (_devicesChanged is null) StopPolling();
			}
		}
	}

	public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDeviceDirection direction)
	{
		lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
		return TryEnumerate(direction) ?? [];
	}

	public AudioDeviceInfo? GetDefaultDevice(AudioDeviceDirection direction) =>
		EnumerateDevices(direction).FirstOrDefault(device => device.IsDefault);

	public AudioDeviceInfo? FindDevice(AudioDeviceDirection direction, string? deviceId)
	{
		if (deviceId is not null && !IsValidDeviceId(deviceId)) return null;
		return string.IsNullOrWhiteSpace(deviceId) ? GetDefaultDevice(direction)
			: EnumerateDevices(direction).FirstOrDefault(device => device.Id == deviceId);
	}

	public IAudioCaptureDevice CreateCapture(string? deviceId = null)
	{
		lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
		return NativeAudioFactory.OpenCapture(deviceId);
	}

	public void NotifySystemPowerResume() => Publish(new(AudioDeviceChangeKind.SystemWake));

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			_devicesChanged = null;
			StopPolling();
		}
	}

	private void StopPolling()
	{
		_timer?.Dispose();
		_timer = null;
		_snapshots.Clear();
		_generation++;
	}

	private void Poll()
	{
		if (Interlocked.Exchange(ref _polling, 1) != 0) return;
		try
		{
			int generation;
			lock (_gate) generation = _generation;
			foreach (AudioDeviceDirection direction in Enum.GetValues<AudioDeviceDirection>())
			{
				IReadOnlyList<AudioDeviceInfo>? current = TryEnumerate(direction);
				if (current is null) continue;
				IReadOnlyList<AudioDeviceInfo>? previous;
				lock (_gate)
				{
					if (_disposed || _timer is null || generation != _generation) return;
					_snapshots.TryGetValue(direction, out previous);
					_snapshots[direction] = current;
				}
				if (previous is null) continue;
				foreach (AudioDeviceChangedEventArgs change in CompareSnapshots(direction, previous, current))
					Publish(change, generation);
			}
		}
		finally { Volatile.Write(ref _polling, 0); }
	}

	internal static IEnumerable<AudioDeviceChangedEventArgs> CompareSnapshots(AudioDeviceDirection direction,
		IReadOnlyList<AudioDeviceInfo> previous, IReadOnlyList<AudioDeviceInfo> current)
	{
		foreach (AudioDeviceInfo old in previous)
		{
			AudioDeviceInfo? device = current.FirstOrDefault(device => device.Id == old.Id);
			if (device is null) yield return new(AudioDeviceChangeKind.Removed, direction, old.Id, old.IsDefault);
			else if (old.DefaultSampleRate != device.DefaultSampleRate || old.DefaultChannels != device.DefaultChannels)
				yield return new(AudioDeviceChangeKind.FormatChanged, direction, device.Id, old.IsDefault && device.IsDefault);
		}
		string? oldDefault = previous.FirstOrDefault(device => device.IsDefault)?.Id;
		string? newDefault = current.FirstOrDefault(device => device.IsDefault)?.Id;
		if (oldDefault != newDefault) yield return new(AudioDeviceChangeKind.DefaultChanged, direction, newDefault);
		foreach (AudioDeviceInfo device in current)
		{
			if (!previous.Any(old => old.Id == device.Id))
				yield return new(AudioDeviceChangeKind.Added, direction, device.Id, device.IsDefault);
		}
	}

	private void Publish(AudioDeviceChangedEventArgs change, int? generation = null)
	{
		Action<AudioDeviceChangedEventArgs>? listeners;
		lock (_gate) listeners = !_disposed && (generation is null || generation == _generation) ? _devicesChanged : null;
		if (listeners is null) return;
		foreach (Action<AudioDeviceChangedEventArgs> listener in listeners.GetInvocationList().Cast<Action<AudioDeviceChangedEventArgs>>())
		{
			try { listener(change); }
			catch (Exception error) { Trace.TraceWarning("音频设备变更通知处理失败：{0}", error.GetType().Name); }
		}
	}

	private IReadOnlyList<AudioDeviceInfo>? TryEnumerate(AudioDeviceDirection direction)
	{
		if (direction is not (AudioDeviceDirection.Input or AudioDeviceDirection.Output))
			throw new ArgumentOutOfRangeException(nameof(direction), "音频设备方向无效。");
		try { return _enumerate(direction); }
		catch (Exception error) when (error is ExternalException or DllNotFoundException or EntryPointNotFoundException
			or AudioDeviceException or BadImageFormatException)
		{
			Trace.TraceWarning("枚举音频设备失败：{0}", error.GetType().Name);
			return null;
		}
	}

	private static IReadOnlyList<AudioDeviceInfo> EnumeratePlatform(AudioDeviceDirection direction)
	{
		if (OperatingSystem.IsWindows()) return WasapiDeviceCatalog.Enumerate(direction);
		if (OperatingSystem.IsMacOS()) return CoreAudioDeviceCatalog.Enumerate(direction);
		if (OperatingSystem.IsLinux()) return AlsaDeviceCatalog.Enumerate(direction);
		return [];
	}

	internal static bool IsValidDeviceId(string id) => id.Length <= MaxDeviceIdLength && !id.Any(char.IsControl);

	internal static string? NormalizeDeviceId(string? id)
	{
		if (id is not null && !IsValidDeviceId(id))
			throw new AudioDeviceException("音频设备标识过长或包含控制字符。");
		return string.IsNullOrWhiteSpace(id) ? null : id;
	}

	internal static AudioDeviceInfo Describe(string id, string name, AudioDeviceDirection direction, bool isDefault,
		AudioFormat? format = null, bool bluetooth = false, string? note = null)
	{
		bluetooth |= IsBluetoothName(id) || IsBluetoothName(name);
		if (bluetooth) note = note is null ? BluetoothNote : $"{note} {BluetoothNote}";
		return new AudioDeviceInfo(id, name, direction, isDefault, bluetooth, !bluetooth,
			format?.SampleRate, format?.Channels, note);
	}

	private static bool IsBluetoothName(string value) =>
		value.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("bluez_", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("hands-free", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("hands free", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("HFP", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("蓝牙", StringComparison.Ordinal);
}
