using System.Runtime.InteropServices;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Linux;

internal static class AlsaDeviceCatalog
{
	private const string Library = "libasound.so.2";

	internal static IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceDirection direction)
	{
		Dictionary<string, AudioDeviceInfo> devices = new(StringComparer.Ordinal)
		{
			["default"] = NativeAudioDeviceManager.Describe("default", "系统默认音频设备", direction, true),
		};
		try
		{
			if (snd_device_name_hint(-1, "pcm", out nint hints) < 0 || hints == 0)
				return devices.Values.ToArray();
			try
			{
				string io = direction == AudioDeviceDirection.Input ? "Input" : "Output";
				for (int offset = 0; ; offset += IntPtr.Size)
				{
					nint hint = Marshal.ReadIntPtr(hints, offset);
					if (hint == 0) break;
					string? supported = ReadHint(hint, "IOID");
					if (supported is not null && supported != io) continue;
					string? id = ReadHint(hint, "NAME");
					if (id is null or "null" || !NativeAudioDeviceManager.IsValidDeviceId(id)) continue;
					string? description = ReadHint(hint, "DESC")?.Split('\n', 2)[0].Trim();
					devices[id] = NativeAudioDeviceManager.Describe(id,
						string.IsNullOrWhiteSpace(description) ? id : description, direction, id == "default");
				}
			}
			finally
			{
				snd_device_name_free_hint(hints);
			}
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
		{
			// 没有 ALSA 枚举接口时仍保留默认路由；打开时才报告库缺失。
		}
		return devices.Values.ToArray();
	}

	private static string? ReadHint(nint hint, string key)
	{
		nint value = snd_device_name_get_hint(hint, key);
		if (value == 0) return null;
		try { return Marshal.PtrToStringUTF8(value); }
		finally { free(value); }
	}

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int snd_device_name_hint(int card, string iface, out nint hints);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern nint snd_device_name_get_hint(nint hint, string id);

	[DllImport(Library)]
	private static extern int snd_device_name_free_hint(nint hints);

	[DllImport("libc")]
	private static extern void free(nint memory);
}
