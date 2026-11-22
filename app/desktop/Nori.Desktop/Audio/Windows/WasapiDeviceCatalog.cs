using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Voice.Audio;
using static Nori.Desktop.Audio.Windows.WasapiNativeApi;

namespace Nori.Desktop.Audio.Windows;

[SupportedOSPlatform("windows")]
internal static class WasapiDeviceCatalog
{
	private const uint Active = 1;
	private const int NotFound = unchecked((int) 0x80070490);
	internal const int DeviceInUse = unchecked((int) 0x8889000A);

	internal static IMmDevice SelectEndpoint(IMmDeviceEnumerator enumerator, int flow, string? deviceId)
	{
		if (deviceId is null)
		{
			enumerator.GetDefaultAudioEndpoint(flow, RoleConsole, out IMmDevice fallback);
			return fallback;
		}
		enumerator.GetDevice(deviceId, out IMmDevice endpoint);
		try
		{
			endpoint.GetState(out uint state);
			((IMmEndpoint) endpoint).GetDataFlow(out int actualFlow);
			if (state != Active || actualFlow != flow)
				throw new AudioDeviceException("所选音频设备不可用或方向不匹配，请重新选择设备。");
			return endpoint;
		}
		catch
		{
			Marshal.ReleaseComObject(endpoint);
			throw;
		}
	}

	internal static IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceDirection direction)
	{
		int flow = direction == AudioDeviceDirection.Input ? DataFlowCapture : DataFlowRender;
		IMmDeviceEnumerator enumerator = (IMmDeviceEnumerator) new MmDeviceEnumerator();
		try
		{
			string? defaultId = null;
			try
			{
				IMmDevice endpoint = SelectEndpoint(enumerator, flow, null);
				try { defaultId = ReadId(endpoint); }
				finally { Marshal.ReleaseComObject(endpoint); }
			}
			catch (COMException error) when (error.HResult == NotFound) { }

			enumerator.EnumAudioEndpoints(flow, Active, out IMmDeviceCollection endpoints);
			try
			{
				List<AudioDeviceInfo> devices = [];
				endpoints.GetCount(out uint count);
				for (uint index = 0; index < count; index++)
				{
					endpoints.Item(index, out IMmDevice endpoint);
					try
					{
						string id = ReadId(endpoint);
						string name = FriendlyName(endpoint);
						devices.Add(NativeAudioDeviceManager.Describe(id, string.IsNullOrWhiteSpace(name) ? id : name,
							direction, id == defaultId, ReadMixFormat(endpoint)));
					}
					finally { Marshal.ReleaseComObject(endpoint); }
				}
				return devices;
			}
			finally { Marshal.ReleaseComObject(endpoints); }
		}
		finally { Marshal.ReleaseComObject(enumerator); }
	}

	private static string ReadId(IMmDevice endpoint)
	{
		endpoint.GetId(out nint id);
		try { return Marshal.PtrToStringUni(id) ?? throw new AudioDeviceException("无法读取音频设备标识。"); }
		finally { CoTaskMemFree(id); }
	}

	private static AudioFormat? ReadMixFormat(IMmDevice endpoint)
	{
		try
		{
			Guid iid = IidAudioClient;
			endpoint.Activate(ref iid, 1, 0, out object client);
			try
			{
				((IAudioClient) client).GetMixFormat(out nint data);
				try
				{
					WaveFormatEx format = Marshal.PtrToStructure<WaveFormatEx>(data);
					return new AudioFormat((int) format.SamplesPerSecond, format.Channels);
				}
				finally { CoTaskMemFree(data); }
			}
			finally { Marshal.ReleaseComObject(client); }
		}
		catch (COMException) { return null; }
	}
}
