namespace Nori.Core.Voice.Audio;

public enum AudioDeviceDirection
{
	Input,
	Output,
}

/// <summary>平台未提供默认格式时，采样率和声道数为空；双工标记仅供提示。</summary>
public sealed record AudioDeviceInfo(
	string Id, string Name, AudioDeviceDirection Direction, bool IsDefault,
	bool IsBluetooth, bool SupportsFullDuplexHint, int? DefaultSampleRate, int? DefaultChannels, string? Note);

public enum AudioDeviceChangeKind
{
	Added,
	Removed,
	DefaultChanged,
	FormatChanged,
	Disconnected,
	SystemWake,
}

/// <summary>
/// 全局变化（例如系统唤醒）不指定方向或设备。拓扑事件可指明设备是否属于默认路由；
/// 空表示未知或不适用。
/// </summary>
public sealed record AudioDeviceChangedEventArgs(
	AudioDeviceChangeKind Kind, AudioDeviceDirection? Direction = null, string? DeviceId = null, bool? IsDefault = null);

public interface IAudioDeviceManager : IDisposable
{
	IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDeviceDirection direction);
	AudioDeviceInfo? GetDefaultDevice(AudioDeviceDirection direction);
	AudioDeviceInfo? FindDevice(AudioDeviceDirection direction, string? deviceId);

	/// <summary>通知可能来自后台线程；系统唤醒由宿主转发。</summary>
	event Action<AudioDeviceChangedEventArgs>? DevicesChanged;
	void NotifySystemPowerResume();
}

/// <summary>创建选定的输入设备；由调用方打开、停止和释放设备句柄。</summary>
public interface IAudioCaptureSource
{
	IAudioCaptureDevice CreateCapture(string? deviceId = null);
	event Action<AudioDeviceChangedEventArgs>? DevicesChanged;
}
