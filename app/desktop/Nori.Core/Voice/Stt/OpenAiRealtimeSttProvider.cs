using Nori.Core.Configuration;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// OpenAI Realtime Transcription 流式语音识别提供商。
///
/// 采用 OpenAI Realtime GA 转写会话协议 (<c>wss://.../v1/realtime?intent=transcription</c>)，
/// 默认模型为 <c>gpt-live-transcribe</c>，输入格式为 24kHz 单声道 PCM16。
/// </summary>
public sealed class OpenAiRealtimeSttProvider : ISttProvider
{
	/// <summary>默认实时转写模型。</summary>
	public const string DefaultModel = "gpt-live-transcribe";

	private readonly ConfigStore? _config;
	private readonly Func<IRealtimeWebSocketTransport> _transportFactory;

	/// <summary>构造实时识别提供商；测试可注入自定义 WebSocket 传输工厂。</summary>
	public OpenAiRealtimeSttProvider(
		ConfigStore? config = null,
		Func<IRealtimeWebSocketTransport>? transportFactory = null)
	{
		_config = config;
		_transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
	}

	/// <inheritdoc />
	public string ProviderId => "openai_realtime";

	/// <inheritdoc />
	public SttMode Mode => SttMode.RealtimeCloud;

	/// <inheritdoc />
	public SttProviderCapabilities Capabilities { get; } = new()
	{
		SupportsBatch = false,
		SupportsRealtimePcm = true,
		SupportsIncrementalDelta = true,
		SupportsLanguageHint = true,
		SupportsLocalOffline = false,
		SupportsHardwareAcceleration = false,
		RequiredSampleRate = 24000,
		RequiredChannels = 1,
	};

	/// <inheritdoc />
	public Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("OpenAI Realtime 语音识别仅支持实时流式 PCM 会话，不支持整段文件转写");

	/// <inheritdoc />
	public async Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (options.ConnectTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "连接超时必须大于 0");
		}
		if (options.SessionReadyTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "会话就绪超时必须大于 0");
		}
		if (options.HeartbeatInterval < TimeSpan.Zero && options.HeartbeatInterval != Timeout.InfiniteTimeSpan)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "心跳间隔不能为负数");
		}
		if (options.EventChannelCapacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "事件通道容量必须大于 0");
		}
		if (options.EventBackpressureTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "事件背压超时必须大于 0");
		}
		if (options.CloseTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "关闭握手超时必须大于 0");
		}

		string? baseUrl = !string.IsNullOrWhiteSpace(options.BaseUrl)
			? options.BaseUrl
			: _config?.GetStringOr("stt_base_url", "");
		Uri endpoint = OpenAiRealtimeSttUriBuilder.Build(baseUrl);

		string? rawApiKey = !string.IsNullOrWhiteSpace(options.ApiKey)
			? options.ApiKey
			: _config?.GetStringOr("stt_api_key", "");
		string apiKey = SttSanitizer.NormalizeApiKey(rawApiKey);

		string model = !string.IsNullOrWhiteSpace(options.Model)
			? options.Model.Trim()
			: DefaultModel;

		IRealtimeWebSocketTransport transport = _transportFactory()
			?? throw new InvalidOperationException("实时语音 WebSocket 传输工厂返回空实例");

		OpenAiRealtimeSttSession session = new(
			ProviderId,
			transport,
			apiKey,
			options.EventChannelCapacity,
			options.EventBackpressureTimeout,
			options.CloseTimeout);
		try
		{
			await session.InitializeAsync(
				endpoint,
				model,
				options.Language,
				options.ConnectTimeout,
				options.SessionReadyTimeout,
				options.HeartbeatInterval,
				cancellationToken).ConfigureAwait(false);
			return session;
		}
		catch
		{
			await session.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}
}
