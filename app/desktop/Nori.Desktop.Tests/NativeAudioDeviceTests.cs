using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio;

namespace Nori.Desktop.Tests;

public sealed class NativeAudioDeviceTests
{
	[Fact]
	public void NativeAudioDeviceManager_当前平台枚举不需要打开音频设备()
	{
		using NativeAudioDeviceManager manager = NativeAudioFactory.CreateDeviceManager();
		foreach (AudioDeviceDirection direction in Enum.GetValues<AudioDeviceDirection>())
		{
			IReadOnlyList<AudioDeviceInfo> devices = manager.EnumerateDevices(direction);
			Assert.All(devices, device =>
			{
				Assert.Equal(direction, device.Direction);
				Assert.False(string.IsNullOrWhiteSpace(device.Id));
			});
			Assert.True(devices.Count(device => device.IsDefault) <= 1);
		}
	}

	[Fact]
	public void NativeAudioDeviceManager_FindDevice区分空默认值指定值方向和无效值()
	{
		AudioDeviceInfo mic = NativeAudioDeviceManager.Describe("mic", "麦克风", AudioDeviceDirection.Input, true);
		AudioDeviceInfo usb = NativeAudioDeviceManager.Describe("usb", "USB 麦克风", AudioDeviceDirection.Input, false);
		AudioDeviceInfo speaker = NativeAudioDeviceManager.Describe("out", "扬声器", AudioDeviceDirection.Output, true);
		using NativeAudioDeviceManager manager = new(direction => direction == AudioDeviceDirection.Input ? [mic, usb] : [speaker]);
		Assert.Equal(mic, manager.FindDevice(AudioDeviceDirection.Input, null));
		Assert.Equal(mic, manager.FindDevice(AudioDeviceDirection.Input, "   "));
		Assert.Equal(speaker, manager.GetDefaultDevice(AudioDeviceDirection.Output));
		Assert.Equal(usb, manager.FindDevice(AudioDeviceDirection.Input, "usb"));
		Assert.Null(manager.FindDevice(AudioDeviceDirection.Output, "usb"));
		Assert.Null(manager.FindDevice(AudioDeviceDirection.Input, "missing"));
		Assert.Null(manager.FindDevice(AudioDeviceDirection.Input, "\n"));
	}

	[Fact]
	public void NativeAudioDeviceManager_快照区分加入移除格式变化与默认变化()
	{
		const AudioDeviceDirection direction = AudioDeviceDirection.Input;
		AudioDeviceInfo old = NativeAudioDeviceManager.Describe("old", "旧麦克风", direction, true);
		AudioDeviceInfo usb = NativeAudioDeviceManager.Describe("usb", "USB 麦克风", direction, false, new AudioFormat(48_000, 2));
		AudioDeviceInfo added = NativeAudioDeviceManager.Describe("new", "新麦克风", direction, true);
		AudioDeviceInfo changed = usb with { DefaultChannels = 1 };

		Assert.Equal([
			new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Removed, direction, "old", true),
			new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.FormatChanged, direction, "usb", false),
			new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.DefaultChanged, direction, "new"),
			new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Added, direction, "new", true),
		], NativeAudioDeviceManager.CompareSnapshots(direction, [old, usb], [changed, added]));
	}

	[Fact]
	public void NativeAudioDeviceManager_唤醒通知转发且释放后停止通知()
	{
		using NativeAudioDeviceManager manager = new(_ => []);
		List<AudioDeviceChangedEventArgs> changes = [];
		manager.DevicesChanged += changes.Add;
		manager.NotifySystemPowerResume();
		manager.Dispose();
		manager.NotifySystemPowerResume();
		Assert.Equal(new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.SystemWake), Assert.Single(changes));
	}

	[Fact]
	public void NativeAudioFactory_不存在的设备不能回落到默认设备()
	{
		if (!System.OperatingSystem.IsLinux() && !System.OperatingSystem.IsWindows() && !System.OperatingSystem.IsMacOS()) return;
		const string missing = "nori-test-unavailable-audio-endpoint";
		using IAudioCaptureDevice capture = NativeAudioFactory.OpenCapture(missing);
		Assert.Throws<AudioDeviceException>(() => capture.Open());
		using IAudioDevice output = NativeAudioFactory.OpenOutput(missing);
		Assert.Throws<AudioDeviceException>(() => output.Open(48_000, 2));
	}

	[Theory]
	[InlineData("\0")]
	[InlineData("usb\nmic")]
	public void NativeAudioFactory_拒绝控制字符(string id) => AssertRejected(id);

	[Fact]
	public void NativeAudioFactory_拒绝过长标识() => AssertRejected(new string('x', 1025));

	[Theory]
	[InlineData("bluez_input.headset", "耳机")]
	[InlineData("endpoint", "Headset Hands-Free AG Audio")]
	[InlineData("endpoint", "Headset hfp")]
	[InlineData("endpoint", "Bluetooth Audio")]
	public void NativeAudioDeviceManager_识别蓝牙并说明双向音频限制(string id, string name)
	{
		AudioDeviceInfo device = NativeAudioDeviceManager.Describe(id, name, AudioDeviceDirection.Input, true);
		Assert.True(device.IsBluetooth);
		Assert.False(device.SupportsFullDuplexHint);
		Assert.Contains("A2DP", device.Note);
		Assert.Contains("HFP", device.Note);
	}

	private static void AssertRejected(string id)
	{
		Assert.Contains("设备标识", Assert.Throws<AudioDeviceException>(() => NativeAudioFactory.OpenCapture(id)).Message);
		Assert.Contains("设备标识", Assert.Throws<AudioDeviceException>(() => NativeAudioFactory.OpenOutput(id)).Message);
	}
}
