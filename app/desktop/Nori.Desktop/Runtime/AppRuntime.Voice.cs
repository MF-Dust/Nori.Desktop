using Nori.Core.Logging;
using Nori.Core.Security;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Audio;

namespace Nori.Desktop.Runtime;

/// <summary>聊天窗口专用的内存状态，不进入通用快照或桥接序列化。</summary>
internal sealed record ChatVoiceSnapshot(RealtimeVoiceStatus Status, bool Speaking, double InputLevel, RealtimeTranscriptSnapshot? Transcript);

public sealed partial class AppRuntime
{
	private double _microphoneTestLevel;
	private string _openedOutputDeviceId = "";

	internal object ListAudioDevices(bool cached = false)
	{
		IReadOnlyList<AudioDeviceInfo> Read(AudioDeviceDirection direction) =>
			cached && _audioDevices is NativeAudioDeviceManager native && native.GetCachedDevices(direction) is { } devices
				? devices : _audioDevices.EnumerateDevices(direction);
		static object Describe(AudioDeviceInfo device) => new
		{
			id = device.Id, name = device.Name, isDefault = device.IsDefault, isBluetooth = device.IsBluetooth, note = device.Note,
		};
		return new
		{
			inputs = Read(AudioDeviceDirection.Input).Select(Describe).ToArray(),
			outputs = Read(AudioDeviceDirection.Output).Select(Describe).ToArray(),
		};
	}

	private void OnAudioDevicesChanged(AudioDeviceChangedEventArgs change)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		try
		{
			if (change.Direction != AudioDeviceDirection.Output || !Voice.IsSpeaking) return;
			string output = _playback.IsPlaying ? Volatile.Read(ref _openedOutputDeviceId)
				: RealtimeVoiceSettings.ReadOutputDeviceId(Services.Config);
			bool affected = change.Kind switch
			{
				AudioDeviceChangeKind.DefaultChanged => output.Length == 0,
				AudioDeviceChangeKind.Removed or AudioDeviceChangeKind.Disconnected =>
					output.Length == 0 ? change.IsDefault != false : change.DeviceId == output,
				_ => false,
			};
			if (affected)
			{
				TrackTask(Task.Run(() =>
				{
					if (Volatile.Read(ref _disposed) != 0) return;
					try { Voice.RecoverPlayback(); }
					catch (Exception exception)
					{
						Services.Logger.Write(LogSource.Backend, "warn",
							$"音频设备变更后恢复播放状态失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
					}
				}));
			}
		}
		finally { InvalidateSnapshot(); }
	}

	internal double MicrophoneTestLevel => Volatile.Read(ref _microphoneTestLevel);

	internal ChatVoiceSnapshot ReadChatVoice()
	{
		var (status, level, transcript) = RealtimeVoice.ReadPresentation();
		return new(status, Voice.IsSpeaking, level, transcript);
	}

	internal Task TestMicrophoneAsync(CancellationToken cancellationToken)
	{
		if (Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("应用正在退出");
		CancellationToken lifetime = _lifetimeCts.Token;
		Task testTask = Task.Run(() =>
		{
			using var test = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
			test.Token.ThrowIfCancellationRequested();
			RealtimeVoiceSettings settings = RealtimeVoiceSettings.Read(Services.Config);
			if (settings.InputDeviceInvalid)
				throw new InvalidOperationException("麦克风标识无效");
			test.CancelAfter(TimeSpan.FromSeconds(10));
			CaptureMicrophoneLevel(_captureOwnership, () => _openCapture(settings.InputDeviceId.Length == 0 ? null : settings.InputDeviceId),
				level => Volatile.Write(ref _microphoneTestLevel, level), test.Token);
		}, cancellationToken);
		TrackTask(testTask);
		return testTask;
	}

	internal static void CaptureMicrophoneLevel(CaptureOwnership ownership, Func<IAudioCaptureDevice> open,
		Action<double> publishLevel, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (!ownership.TryAcquire()) throw new InvalidOperationException("麦克风正被占用，请先停止其他录音或实时监听");
		try
		{
			using IAudioCaptureDevice device = open();
			AudioFormat format = device.Open();
			// 页面取消令牌可能来自 UI；停止原生设备必须在后台执行，并在释放设备前等待停止结束。
			Task? stopping = null;
			using CancellationTokenRegistration stop = token.Register(() => stopping = Task.Run(() =>
			{
				try { device.Stop(); } catch (AudioDeviceException) { /* 仍由采集线程释放设备。 */ }
			}));
			float[] samples = new float[1024 * Math.Max(1, format.Channels)];
			double level = 0;
			try
			{
				while (!token.IsCancellationRequested)
				{
					int count = device.Read(samples, token);
					if (token.IsCancellationRequested) break;
					if (count <= 0) throw new InvalidOperationException("麦克风测试已中断");
					double energy = 0;
					for (int index = 0; index < count; index++)
						if (float.IsFinite(samples[index])) energy += (double)samples[index] * samples[index];
					level = level * 0.65 + Math.Clamp(Math.Sqrt(energy / count), 0, 1) * 0.35;
					publishLevel(level);
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) { }
			finally
			{
				stop.Dispose();
				Array.Clear(samples);
				stopping?.GetAwaiter().GetResult();
			}
		}
		finally
		{
			publishLevel(0);
			ownership.Release();
		}
	}
}
