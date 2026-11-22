namespace Nori.Core.Voice.Stt;

/// <summary>本地离线语音识别引擎类型。</summary>
public enum LocalSttEngineKind
{
	/// <summary>基于 whisper.cpp 的本地推理引擎。</summary>
	WhisperCpp,

	/// <summary>基于 sherpa-onnx 的本地推理引擎。</summary>
	SherpaOnnx,

	/// <summary>用户自定义的本地离线引擎。</summary>
	Custom,
}

/// <summary>本地离线语音识别模型与运行配置。</summary>
public sealed record LocalSttModelConfig
{
	/// <summary>默认推理线程数。</summary>
	public const int DefaultThreads = 4;

	/// <summary>默认单次离线转写超时时间。</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	/// <summary>目标本地引擎类型。</summary>
	public LocalSttEngineKind EngineKind { get; init; } = LocalSttEngineKind.WhisperCpp;

	/// <summary>本地模型文件或模型目录路径。</summary>
	public string ModelPath { get; init; } = string.Empty;

	/// <summary>推理线程数。</summary>
	public int Threads { get; init; } = DefaultThreads;

	/// <summary>是否启用硬件加速（如 GPU / Metal / Vulkan / DirectML）。</summary>
	public bool UseHardwareAcceleration { get; init; }

	/// <summary>语言提示（如 zh、en）；为空时交由模型自动识别。</summary>
	public string? Language { get; init; } = "zh";

	/// <summary>单次本地转写超时时间。</summary>
	public TimeSpan Timeout { get; init; } = DefaultTimeout;
}

/// <summary>
/// 本地离线语音识别引擎扩展接缝。
///
/// 由外部离线语音扩展或受控本地进程适配器实现；
/// 宿主默认不内置模型权重，也不自动下载任何模型文件。
/// </summary>
public interface ILocalSttEngine
{
	/// <summary>引擎类型。</summary>
	LocalSttEngineKind EngineKind { get; }

	/// <summary>当前引擎实际支持的能力声明。</summary>
	SttProviderCapabilities Capabilities { get; }

	/// <summary>引擎运行环境是否已在本地就绪。</summary>
	bool IsAvailable { get; }

	/// <summary>执行本地整段音频离线转写。</summary>
	Task<string> TranscribeLocalAsync(
		RecordedAudio audio,
		LocalSttModelConfig config,
		CancellationToken cancellationToken = default);

	/// <summary>打开本地实时流式识别会话；不支持流式时抛出 <see cref="NotSupportedException"/>。</summary>
	Task<IStreamingSttSession> OpenLocalStreamAsync(
		LocalSttModelConfig config,
		CancellationToken cancellationToken = default);
}
