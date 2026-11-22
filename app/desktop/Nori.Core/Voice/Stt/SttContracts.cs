using System.Threading.Channels;

namespace Nori.Core.Voice.Stt;

/// <summary>语音识别提供商运行模式。</summary>
public enum SttMode
{
	/// <summary>云端实时流式识别。</summary>
	RealtimeCloud,

	/// <summary>云端整段文件转写。</summary>
	BatchCloud,

	/// <summary>本地离线识别。</summary>
	Local,
}

/// <summary>语音识别提供商能力声明。</summary>
public sealed record SttProviderCapabilities
{
	/// <summary>是否支持整段录音文件转写。</summary>
	public bool SupportsBatch { get; init; }

	/// <summary>是否支持实时 PCM 流式输入。</summary>
	public bool SupportsRealtimePcm { get; init; }

	/// <summary>是否支持增量 delta 文本输出。</summary>
	public bool SupportsIncrementalDelta { get; init; }

	/// <summary>是否支持语言提示。</summary>
	public bool SupportsLanguageHint { get; init; }

	/// <summary>是否支持本地离线运行。</summary>
	public bool SupportsLocalOffline { get; init; }

	/// <summary>是否支持硬件加速。</summary>
	public bool SupportsHardwareAcceleration { get; init; }

	/// <summary>期望输入采样率 (Hz)。</summary>
	public int RequiredSampleRate { get; init; } = 16000;

	/// <summary>期望输入声道数。</summary>
	public int RequiredChannels { get; init; } = 1;
}

/// <summary>语音识别请求参数。</summary>
public sealed record SttRequestOptions
{
	/// <summary>指定识别模型；为空时使用提供商默认模型。</summary>
	public string? Model { get; init; }

	/// <summary>语言提示 (如 zh、en)；为空时省略或交由服务端自动识别。</summary>
	public string? Language { get; init; }

	/// <summary>服务端点基地址；为空时从配置或默认值解析。</summary>
	public string? BaseUrl { get; init; }

	/// <summary>鉴权密钥；为空时从配置读取。</summary>
	public string? ApiKey { get; init; }

	/// <summary>连接超时时间。</summary>
	public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>等待会话就绪超时时间。</summary>
	public TimeSpan SessionReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>WebSocket 心跳保活间隔。</summary>
	public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

	/// <summary>流式事件通道容量上限。</summary>
	public int EventChannelCapacity { get; init; } = 256;

	/// <summary>事件通道满时的背压等待超时。</summary>
	public TimeSpan EventBackpressureTimeout { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>主动关闭会话时的发送锁与握手超时。</summary>
	public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>流式语音识别事件类型。</summary>
public enum StreamingSttEventKind
{
	/// <summary>服务端确认会话配置已更新。</summary>
	SessionUpdated,

	/// <summary>服务端确认输入音频缓冲区已提交为条目。</summary>
	InputCommitted,

	/// <summary>转写增量片段。</summary>
	Delta,

	/// <summary>单条语音转写完成。</summary>
	Completed,

	/// <summary>单条语音转写失败。</summary>
	Failed,

	/// <summary>会话或传输层错误。</summary>
	Error,

	/// <summary>连接已关闭。</summary>
	Closed,
}

/// <summary>流式语音识别事件。</summary>
public sealed record StreamingSttEvent
{
	/// <summary>事件类型。</summary>
	public required StreamingSttEventKind Kind { get; init; }

	/// <summary>服务端关联的条目编号。</summary>
	public string? ItemId { get; init; }

	/// <summary>内容段索引。</summary>
	public int? ContentIndex { get; init; }

	/// <summary>增量文本或最终转写结果。</summary>
	public string? Text { get; init; }

	/// <summary>服务端或协议错误码。</summary>
	public string? ErrorCode { get; init; }

	/// <summary>服务端或协议错误分类。</summary>
	public string? ErrorType { get; init; }

	/// <summary>经脱敏处理后的错误消息。</summary>
	public string? ErrorMessage { get; init; }

	/// <summary>会话内单调递增事件序号。</summary>
	public long Sequence { get; init; }
}

/// <summary>流式语音识别会话。</summary>
public interface IStreamingSttSession : IAsyncDisposable
{
	/// <summary>提供商标识。</summary>
	string ProviderId { get; }

	/// <summary>当前是否处于已连接且就绪状态。</summary>
	bool IsConnected { get; }

	/// <summary>识别事件读取通道。</summary>
	ChannelReader<StreamingSttEvent> Events { get; }

	/// <summary>追加一帧 24kHz 单声道 PCM16 小端字节数据。</summary>
	Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16Mono24k, CancellationToken cancellationToken = default);

	/// <summary>提交当前音频缓冲区触发转写。</summary>
	Task CommitAsync(CancellationToken cancellationToken = default);

	/// <summary>主动关闭会话并等待后台接收循环结束。</summary>
	Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>语音识别提供商统一抽象。</summary>
public interface ISttProvider
{
	/// <summary>提供商稳定标识。</summary>
	string ProviderId { get; }

	/// <summary>运行模式。</summary>
	SttMode Mode { get; }

	/// <summary>能力声明。</summary>
	SttProviderCapabilities Capabilities { get; }

	/// <summary>整段录音转写；不支持整段时抛出 <see cref="NotSupportedException"/>。</summary>
	Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken = default);

	/// <summary>打开实时流式识别会话；不支持流式时抛出 <see cref="NotSupportedException"/>。</summary>
	Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken = default);
}
