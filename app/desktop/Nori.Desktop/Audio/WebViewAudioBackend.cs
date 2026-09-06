using Nori.Core.Voice;

namespace Nori.Desktop.Audio;

/// <summary>
/// 把音频事件推给某个 WebView 窗口的通道。
///
/// 音频宿主通过 audio_host_ready 握手；测试替身可以只实现 IsAvailable/Post，
/// 默认把可用视为已就绪。
/// </summary>
public interface IAudioHostChannel
{
	/// <summary>宿主窗口是否存在。</summary>
	bool IsAvailable { get; }

	/// <summary>宿主页面是否已安装音频事件监听器。</summary>
	bool IsReady => IsAvailable;

	/// <summary>等待宿主就绪；默认实现供简单测试替身使用。</summary>
	Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
	{
		if (!IsAvailable || !IsReady) throw new InvalidOperationException("音频宿主窗口尚未就绪");
		return Task.CompletedTask;
	}

	/// <summary>标记宿主页面已就绪；默认实现供简单测试替身使用。</summary>
	void MarkReady()
	{
	}

	/// <summary>向音频宿主窗口推事件。</summary>
	void Post(string name, object? payload);
}

/// <summary>
/// WebView 音频播放后端。
///
/// 平台无关：音频字节经 AssetServer 的一次性媒体端点交给前端，由 WebAudio 播放，
/// 前端每约 60ms 回传一次 RMS 音量驱动口型，播放结束只回报一次终态。
/// </summary>
public sealed class WebViewAudioPlayback(MediaExchange media, Func<string, string> mediaUrl, IAudioHostChannel channel) : IAudioPlayback
{
	/// <summary>事件丢失时的兜底等待上限。</summary>
	private static readonly TimeSpan PlaybackTimeout = TimeSpan.FromMinutes(5);

	private readonly object _gate = new();
	private TaskCompletionSource<bool>? _current;
	private string _currentToken = "";
	private bool _playing;
	private double _deviceVolume = 1.0;

	/// <inheritdoc />
	public bool IsPlaying => Volatile.Read(ref _playing);

	/// <inheritdoc />
	public event Action<bool>? PlayingChanged;

	/// <inheritdoc />
	public event Action<double>? VolumeSampled;

	/// <summary>设置输出音量 (0~1)，随下一段播放生效。</summary>
	public void SetDeviceVolume(double volume) => Volatile.Write(ref _deviceVolume, Math.Clamp(volume, 0, 1));

	/// <inheritdoc />
	public async Task PlayAsync(EncodedAudio audio, CancellationToken cancellationToken)
	{
		EncodedAudio validated = AudioMime.ValidateEncoded(audio.Bytes, audio.Mime);
		await channel.WaitUntilReadyAsync(cancellationToken);
		Stop();

		string token = media.PublishAudio(validated);
		TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
		{
			_current = completion;
			_currentToken = token;
		}
		SetPlaying(true);

		try
		{
			channel.Post("nori:audio-play", new
			{
				token,
				url = mediaUrl(token),
				mime = validated.Mime,
				volume = Volatile.Read(ref _deviceVolume),
			});
		}
		catch
		{
			ClearCurrent(completion, token);
			throw;
		}

		try
		{
			await completion.Task.WaitAsync(PlaybackTimeout, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			try { channel.Post("nori:audio-stop", null); } catch { }
			throw;
		}
		catch (TimeoutException)
		{
			try { channel.Post("nori:audio-stop", null); } catch { }
			throw new TimeoutException("等待前端音频播放结束超时");
		}
		finally
		{
			ClearCurrent(completion, token);
		}
	}

	/// <inheritdoc />
	public void Stop()
	{
		TaskCompletionSource<bool>? completion;
		lock (_gate)
		{
			completion = _current;
			_current = null;
			_currentToken = "";
		}
		if (completion is null) return;
		try { channel.Post("nori:audio-stop", null); }
		catch { /* 宿主已退出，等待方仍须立即结束 */ }
		completion.TrySetResult(false);
		SetPlaying(false);
	}

	/// <summary>前端回报：某段音频播完 / 播放失败。</summary>
	public void ReportPlaybackFinished(string token, string? error)
	{
		TaskCompletionSource<bool>? completion;
		lock (_gate)
		{
			if (_currentToken.Length == 0 || !_currentToken.Equals(token, StringComparison.Ordinal)) return;
			completion = _current;
			_current = null;
			_currentToken = "";
		}
		if (completion is null) return;
		SetPlaying(false);
		if (error is {Length: > 0})
		{
			completion.TrySetException(new InvalidOperationException($"音频播放失败: {error}"));
			return;
		}
		completion.TrySetResult(true);
	}

	/// <summary>前端回报的实时音量 (0~1)，直接驱动口型。</summary>
	public void ReportLevel(double level) => VolumeSampled?.Invoke(Math.Clamp(level, 0, 1));

	private void ClearCurrent(TaskCompletionSource<bool> completion, string token)
	{
		lock (_gate)
		{
			if (!ReferenceEquals(_current, completion) || !_currentToken.Equals(token, StringComparison.Ordinal)) return;
			_current = null;
			_currentToken = "";
		}
		SetPlaying(false);
	}

	private void SetPlaying(bool value)
	{
		if (Interlocked.Exchange(ref _playing, value) == value) return;
		PlayingChanged?.Invoke(value);
	}

	public void Dispose() => Stop();
}

/// <summary>
/// WebView 麦克风录音后端。
///
/// 前端用 MediaRecorder 采集，开始录音后先回报权限/启动结果，结束后把保留实际 MIME
/// 的音频 POST 回一次性媒体端点；全流程异步，绝不在 UI 线程上等待。
/// </summary>
public sealed class WebViewMicrophoneRecorder(MediaExchange media, Func<string, string> mediaUrl, IAudioHostChannel channel) : IMicrophoneRecorder
{
	private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(20);

	private readonly object _gate = new();
	private string _token = "";
	private TaskCompletionSource<bool>? _startCompletion;

	/// <inheritdoc />
	public bool IsRecording
	{
		get { lock (_gate) return _token.Length > 0; }
	}

	/// <inheritdoc />
	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		await channel.WaitUntilReadyAsync(cancellationToken);
		TaskCompletionSource<bool> started;
		string token;
		lock (_gate)
		{
			if (_token.Length > 0) return;
			token = media.CreateUploadTicket();
			_token = token;
			started = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_startCompletion = started;
		}

		try
		{
			channel.Post("nori:audio-record-start", new {token, uploadUrl = mediaUrl(token)});
			await started.Task.WaitAsync(StartTimeout, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			CancelRecording(token);
			throw;
		}
		catch (TimeoutException)
		{
			CancelRecording(token);
			throw new TimeoutException("等待前端麦克风权限结果超时");
		}
		catch
		{
			CancelRecording(token);
			throw;
		}
	}

	/// <inheritdoc />
	public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
	{
		string token;
		TaskCompletionSource<bool>? pendingStart = null;
		bool startPending = false;
		lock (_gate)
		{
			token = _token;
			pendingStart = _startCompletion;
			startPending = pendingStart is { Task.IsCompleted: false };
			if (token.Length > 0 && startPending)
			{
				// 录音尚未就绪就停止: 先摘除当前会话, 走"空录音"分支而不是等上传。
				_token = "";
				_startCompletion = null;
			}
		}
		if (token.Length == 0) return new RecordedAudio([], "audio/wav", "speech.wav");

		if (startPending)
		{
			// 作废票据并解除 StartAsync 的等待; 前端随后对旧 token 的回报都会因 token 不匹配被忽略。
			media.CancelUpload(token);
			pendingStart?.TrySetCanceled();
			try { channel.Post("nori:audio-record-stop", new {token}); } catch { }
			return new RecordedAudio([], "audio/wav", "speech.wav");
		}

		try
		{
			channel.Post("nori:audio-record-stop", new {token});
			return await media.WaitForRecordedUploadAsync(token, UploadTimeout, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			media.CancelUpload(token);
			throw;
		}
		finally
		{
			lock (_gate)
			{
				if (_token.Equals(token, StringComparison.Ordinal))
				{
					_token = "";
					_startCompletion = null;
				}
			}
		}
	}

	/// <summary>前端权限已授予且 MediaRecorder 已开始。</summary>
	public void ReportRecordingReady(string token)
	{
		lock (_gate)
		{
			if (!_token.Equals(token, StringComparison.Ordinal)) return;
			_startCompletion?.TrySetResult(true);
		}
	}

	/// <summary>前端权限、MediaRecorder 或上传失败，立即结束等待。</summary>
	public void ReportRecordingFailed(string token, string? error)
	{
		TaskCompletionSource<bool>? started;
		lock (_gate)
		{
			if (!_token.Equals(token, StringComparison.Ordinal)) return;
			started = _startCompletion;
		}
		if (started is not null && !started.Task.IsCompleted)
		{
			started.TrySetException(new InvalidOperationException(
				string.IsNullOrWhiteSpace(error) ? "前端麦克风不可用" : $"前端麦克风失败: {error}"));
		}
		else
		{
			media.TryFailUpload(token, error ?? "录音上传失败");
		}
	}

	private void CancelRecording(string token)
	{
		media.CancelUpload(token);
		lock (_gate)
		{
			if (_token.Equals(token, StringComparison.Ordinal))
			{
				_token = "";
				_startCompletion = null;
			}
		}
	}

	public void Dispose()
	{
		TaskCompletionSource<bool>? started;
		string token;
		lock (_gate)
		{
			token = _token;
			_token = "";
			started = _startCompletion;
			_startCompletion = null;
		}
		// 解除还在 StartAsync 中等待权限回报的等待者, 避免Dispose后等到超时。
		started?.TrySetCanceled();
		if (token.Length > 0) media.CancelUpload(token);
	}
}

/// <summary>
/// 指向某个 WebView 窗口的音频事件通道。
/// 音频宿主固定为 main 窗口：它关窗只隐藏、进程内始终存在，因此隐藏时依然能放声。
/// </summary>
public class AudioHostChannel(Func<Nori.Desktop.Windows.NoriWindow?> resolve, TimeSpan? readyTimeout = null) : IAudioHostChannel, IDisposable
{
	/// <summary>等待宿主页面握手完成的默认上限; 超过视为宿主异常而不是永远等待。</summary>
	public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

	private readonly object _gate = new();
	private readonly TimeSpan _readyTimeout = readyTimeout ?? ReadyTimeout;
	private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _disposed;

	/// <inheritdoc />
	public virtual bool IsAvailable => !Volatile.Read(ref _disposed) && resolve() is not null;

	/// <inheritdoc />
	public bool IsReady => IsAvailable && _ready.Task.IsCompletedSuccessfully;

	/// <summary>
	/// 真正等待宿主页面完成 audio_host_ready 握手。
	///
	/// 宿主窗口不存在时立刻给出明确 unavailable; 宿主存在但页面未就绪时阻塞等待,
	/// 直到 MarkReady、取消、Dispose 或超时。
	/// </summary>
	public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!IsAvailable) throw new InvalidOperationException("音频宿主窗口不可用");
		try
		{
			await _ready.Task.WaitAsync(_readyTimeout, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			throw new TimeoutException("等待音频宿主就绪超时");
		}
	}

	/// <inheritdoc />
	public void MarkReady() => _ready.TrySetResult(true);

	/// <inheritdoc />
	public void Post(string name, object? payload)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Nori.Desktop.Windows.NoriWindow? window = resolve()
			?? throw new InvalidOperationException("音频宿主窗口不可用");
		window.PostEvent(name, payload);
	}

	/// <summary>解除所有等待者并拒绝后续使用; 由应用关闭路径调用。</summary>
	public virtual void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
		}
		_ready.TrySetCanceled();
	}
}
