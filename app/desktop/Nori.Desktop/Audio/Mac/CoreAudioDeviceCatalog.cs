using System.Runtime.InteropServices;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Mac;

internal static class CoreAudioDeviceCatalog
{
	private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const uint SystemObject = 1;
	private const uint Global = 0x676C6F62; // glob
	private const uint Utf8 = 0x08000100;
	internal const string MicrophonePermissionNote = "录音需要在系统设置的“隐私与安全性 → 麦克风”中授权 Nori。";

	internal static IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceDirection direction)
	{
		uint defaultId = GetDefaultId(direction);
		return ReadDevices().Select(id => Describe(id, defaultId, direction)).OfType<AudioDeviceInfo>().ToArray();
	}

	internal static AudioFormat? GetDefaultFormat(AudioDeviceDirection direction) =>
		Read(GetDefaultId(direction), 0x73666D74, DirectionScope(direction), (data, size) => // sfmt
		{
			if (size < Marshal.SizeOf<CoreAudioQueue.AudioStreamBasicDescription>()) return (AudioFormat?) null;
			CoreAudioQueue.AudioStreamBasicDescription format = Marshal.PtrToStructure<CoreAudioQueue.AudioStreamBasicDescription>(data);
			return format.SampleRate is >= 1 and <= int.MaxValue && format.ChannelsPerFrame is > 0 and <= int.MaxValue
				? new AudioFormat((int) Math.Round(format.SampleRate), (int) format.ChannelsPerFrame) : null;
		});

	internal static bool IsHeldByAnotherProcess(AudioDeviceDirection direction, string? uid)
	{
		uint device = uid is null ? GetDefaultId(direction) : ReadDevices().FirstOrDefault(id => ReadString(id, 0x75696420) == uid);
		int? owner = Read(device, 0x6F696E6B, Global, (data, size) => size == sizeof(int) ? Marshal.ReadInt32(data) : (int?) null);
		return owner > 0 && owner != Environment.ProcessId;
	}

	internal static nint CreateDeviceUid(string uid) => CFStringCreateWithCString(0, uid, Utf8);

	private static uint GetDefaultId(AudioDeviceDirection direction) =>
		Read(SystemObject, direction == AudioDeviceDirection.Input ? 0x64496E20u : 0x644F7574u,
			Global, (data, size) => size == sizeof(uint) ? (uint) Marshal.ReadInt32(data) : 0);

	private static uint[] ReadDevices() => Read(SystemObject, 0x64657623, Global, (data, size) =>
	{
		uint[] ids = new uint[size / sizeof(uint)];
		for (int index = 0; index < ids.Length; index++) ids[index] = (uint) Marshal.ReadInt32(data, index * sizeof(uint));
		return ids;
	}) ?? [];

	private static AudioDeviceInfo? Describe(uint id, uint defaultId, AudioDeviceDirection direction)
	{
		AudioFormat? format = ReadFormat(id, direction);
		if (format is null) return null;
		string? uid = ReadString(id, 0x75696420); // uid
		if (string.IsNullOrEmpty(uid) || !NativeAudioDeviceManager.IsValidDeviceId(uid)) return null;
		string? name = ReadString(id, 0x6C6E616D); // lnam
		uint transport = Read(id, 0x7472616E, Global, (data, size) => size == sizeof(uint) ? (uint) Marshal.ReadInt32(data) : 0);
		return NativeAudioDeviceManager.Describe(uid, string.IsNullOrWhiteSpace(name) ? uid : name, direction,
			id == defaultId, format, bluetooth: transport is 0x626C7565 or 0x626C6561, // blue、blea
			note: direction == AudioDeviceDirection.Input ? MicrophonePermissionNote : null);
	}

	private static AudioFormat? ReadFormat(uint id, AudioDeviceDirection direction)
	{
		int channels = Read(id, 0x736C6179, DirectionScope(direction), (data, size) => // slay：AudioBufferList
		{
			if (size < sizeof(uint)) return 0;
			int bufferCount = Marshal.ReadInt32(data);
			int total = 0;
			int bufferSize = 2 * sizeof(uint) + IntPtr.Size;
			for (int index = 0, offset = IntPtr.Size; index < bufferCount && offset + bufferSize <= size; index++, offset += bufferSize)
				total += Marshal.ReadInt32(data, offset);
			return total;
		});
		if (channels <= 0) return null;
		double rate = Read(id, 0x6E737274, Global, (data, size) => // nsrt
			size == sizeof(double) ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data)) : 0);
		return rate is >= 1 and <= int.MaxValue ? new AudioFormat((int) Math.Round(rate), channels) : null;
	}

	private static uint DirectionScope(AudioDeviceDirection direction) =>
		direction == AudioDeviceDirection.Input ? 0x696E7074u : 0x6F757470u;

	private static string? ReadString(uint id, uint selector) => Read(id, selector, Global, (data, size) =>
	{
		if (size != IntPtr.Size) return null;
		nint value = Marshal.ReadIntPtr(data);
		if (value == 0) return null;
		try
		{
			byte[] buffer = new byte[4096];
			return CFStringGetCString(value, buffer, buffer.Length, Utf8)
				? System.Text.Encoding.UTF8.GetString(buffer.AsSpan(0, Array.IndexOf(buffer, (byte) 0))) : null;
		}
		finally { CFRelease(value); }
	});

	private static T? Read<T>(uint id, uint selector, uint scope, Func<nint, uint, T> convert)
	{
		if (id == 0) return default;
		PropertyAddress address = new(selector, scope, 0);
		if (AudioObjectGetPropertyDataSize(id, ref address, 0, 0, out uint size) != 0 || size is 0 or > 1_048_576)
			return default;
		nint data = Marshal.AllocHGlobal((int) size);
		try { return AudioObjectGetPropertyData(id, ref address, 0, 0, ref size, data) == 0 ? convert(data, size) : default; }
		finally { Marshal.FreeHGlobal(data); }
	}

	[StructLayout(LayoutKind.Sequential)]
	private readonly struct PropertyAddress(uint selector, uint scope, uint element)
	{
		private readonly uint _selector = selector;
		private readonly uint _scope = scope;
		private readonly uint _element = element;
	}

	[DllImport(CoreAudio)]
	private static extern int AudioObjectGetPropertyDataSize(uint id, ref PropertyAddress address, uint qualifierSize, nint qualifier, out uint size);

	[DllImport(CoreAudio)]
	private static extern int AudioObjectGetPropertyData(uint id, ref PropertyAddress address, uint qualifierSize, nint qualifier, ref uint size, nint data);

	[DllImport(CoreFoundation)]
	private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

	[DllImport(CoreFoundation)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint capacity, uint encoding);

	[DllImport(CoreFoundation)]
	internal static extern void CFRelease(nint value);
}
