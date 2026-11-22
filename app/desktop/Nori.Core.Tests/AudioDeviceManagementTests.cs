using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

public sealed class AudioDeviceManagementTests
{
	[Fact]
	public void AudioDeviceContracts_枚举默认设备并保留蓝牙提示()
	{
		using FakeDeviceManager devices = new();
		AudioDeviceInfo input = Assert.Single(devices.EnumerateDevices(AudioDeviceDirection.Input));
		AudioDeviceInfo output = Assert.Single(devices.EnumerateDevices(AudioDeviceDirection.Output));
		Assert.Equal(input, devices.GetDefaultDevice(AudioDeviceDirection.Input));
		Assert.Equal(input, devices.FindDevice(AudioDeviceDirection.Input, null));
		Assert.Equal(output, devices.FindDevice(AudioDeviceDirection.Output, "speaker"));
		Assert.Null(devices.FindDevice(AudioDeviceDirection.Input, "speaker"));
		Assert.True(input.IsBluetooth);
		Assert.False(input.SupportsFullDuplexHint);
		Assert.Contains("HFP", input.Note);
	}

	[Fact]
	public async Task AudioDeviceRecoveryPolicy_热插拔断开格式变化和唤醒先释放旧设备()
	{
		using FakeDeviceManager devices = new();
		FakeCapture capture = new();
		AudioDeviceRecoveryPolicy policy = new(capture.Open, capture.Release, "mic");
		Task change = Task.CompletedTask;
		devices.DevicesChanged += args => change = policy.HandleDeviceChangeAsync(args);
		devices.NotifySystemPowerResume();
		await change;
		Assert.Empty(capture.Calls);
		await policy.MarkUserStartedAsync();

		devices.Raise(AudioDeviceChangeKind.Removed, "other-mic");
		await change;
		Assert.Equal(["打开"], capture.Calls);
		devices.Raise(AudioDeviceChangeKind.Removed, "mic");
		await change;
		Assert.Equal(["打开", "释放"], capture.Calls);
		devices.Raise(AudioDeviceChangeKind.Added, "mic");
		await change;
		devices.Raise(AudioDeviceChangeKind.FormatChanged, "mic");
		await change;
		devices.NotifySystemPowerResume();
		await change;
		devices.Raise(AudioDeviceChangeKind.Disconnected, "mic");
		await change;

		Assert.Equal(["打开", "释放", "打开", "释放", "打开", "释放", "打开", "释放"], capture.Calls);
	}

	[Fact]
	public async Task AudioDeviceRecoveryPolicy_默认路由只响应相关变化且没有替代设备时停止()
	{
		FakeCapture capture = new();
		AudioDeviceRecoveryPolicy policy = new(capture.Open, capture.Release);
		await policy.MarkUserStartedAsync();
		const AudioDeviceDirection input = AudioDeviceDirection.Input;
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.Removed, input, "usb", false));
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.DefaultChanged, AudioDeviceDirection.Output, "speaker"));
		Assert.Equal(["打开"], capture.Calls);
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.DefaultChanged, input));
		Assert.Equal(["打开", "释放"], capture.Calls);
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.DefaultChanged, input, "usb"));
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.Added, input, "usb", true));
		Assert.Equal(["打开", "释放", "打开"], capture.Calls);
		policy.MarkUserStopped();
	}

	[Fact]
	public async Task AudioDeviceRecoveryPolicy_断网停止采集且用户停止后不自动打开()
	{
		FakeCapture capture = new();
		AudioDeviceRecoveryPolicy policy = new(capture.Open, capture.Release);
		await policy.MarkUserStartedAsync();
		await policy.SetNetworkAvailableAsync(false);
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.SystemWake));
		Assert.Equal(["打开", "释放"], capture.Calls);
		await policy.SetNetworkAvailableAsync(true);
		policy.MarkUserStopped();

		foreach (AudioDeviceChangeKind kind in Enum.GetValues<AudioDeviceChangeKind>())
			await policy.HandleDeviceChangeAsync(new(kind, AudioDeviceDirection.Input, "mic"));
		await policy.SetNetworkAvailableAsync(false);
		await policy.SetNetworkAvailableAsync(true);
		Assert.Equal(["打开", "释放", "打开", "释放"], capture.Calls);
	}

	[Fact]
	public async Task AudioDeviceRecoveryPolicy_占用重试有上限且后续事件不能重置上限()
	{
		FakeCapture capture = new() { Busy = true };
		AudioDeviceRecoveryPolicy policy = new(capture.Open, capture.Release);
		Task firstAttempt = policy.MarkUserStartedAsync();
		Task afterWake = policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.SystemWake));
		await firstAttempt;
		AudioDeviceException error = await Assert.ThrowsAsync<AudioDeviceException>(() => afterWake);
		Assert.Contains("麦克风被占用", error.Message);
		Assert.Equal(["打开", "释放", "打开", "释放", "打开", "释放"], capture.Calls);
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.Added));
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.SystemWake));
		Assert.Equal(6, capture.Calls.Count);

		capture.Busy = false;
		await policy.MarkUserStartedAsync();
		Assert.Equal(7, capture.Calls.Count);
		policy.MarkUserStopped();
	}

	[Fact]
	public async Task AudioDeviceRecoveryPolicy_退避期间用户停止会作废待执行的尝试()
	{
		FakeCapture capture = new() { Busy = true };
		AudioDeviceRecoveryPolicy policy = new(capture.Open, capture.Release);
		Task start = policy.MarkUserStartedAsync();
		policy.MarkUserStopped();
		await start;
		await policy.HandleDeviceChangeAsync(new(AudioDeviceChangeKind.SystemWake));
		Assert.Equal(["打开", "释放"], capture.Calls);
	}

	private sealed class FakeDeviceManager : IAudioDeviceManager
	{
		private readonly AudioDeviceInfo[] _devices =
		[
			new("mic", "蓝牙麦克风", AudioDeviceDirection.Input, true, true, false, 16_000, 1, "使用麦克风时可能切换到 HFP。"),
			new("speaker", "扬声器", AudioDeviceDirection.Output, true, false, true, 48_000, 2, null),
		];

		public event Action<AudioDeviceChangedEventArgs>? DevicesChanged;
		public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDeviceDirection direction) =>
			_devices.Where(device => device.Direction == direction).ToArray();
		public AudioDeviceInfo? GetDefaultDevice(AudioDeviceDirection direction) =>
			EnumerateDevices(direction).FirstOrDefault(device => device.IsDefault);
		public AudioDeviceInfo? FindDevice(AudioDeviceDirection direction, string? deviceId) => deviceId is null
			? GetDefaultDevice(direction) : EnumerateDevices(direction).FirstOrDefault(device => device.Id == deviceId);
		public void Raise(AudioDeviceChangeKind kind, string id) => DevicesChanged?.Invoke(new(kind, AudioDeviceDirection.Input, id));
		public void NotifySystemPowerResume() => DevicesChanged?.Invoke(new(AudioDeviceChangeKind.SystemWake));
		public void Dispose() => DevicesChanged = null;
	}

	private sealed class FakeCapture
	{
		public List<string> Calls { get; } = [];
		public bool Busy { get; set; }
		private bool _held;

		public void Open()
		{
			Assert.False(_held, "必须先释放旧句柄");
			_held = true;
			Calls.Add("打开");
			if (Busy) throw new AudioDeviceException("占用") { IsBusy = true };
		}

		public void Release()
		{
			Assert.True(_held);
			_held = false;
			Calls.Add("释放");
		}
	}
}
