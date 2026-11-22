using System.Buffers;
using System.Diagnostics;
using System.Text;
using Nori.Core.Configuration;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// 本地离线语音识别提供商。
///
/// 通过 <see cref="ILocalSttEngine"/> 扩展接缝驱动本地引擎，
/// 严格保证宿主自身不发起任何网络请求、不自动下载模型、不将本地引擎或权重作为强制依赖，
/// 并且如实保留底层引擎的能力声明（若底层未声明 <see cref="SttProviderCapabilities.SupportsLocalOffline"/>，
/// 将拒绝作为本地离线提供商注册或执行）。
/// 安全边界说明：宿主侧本地离线适配器仅运行用户显式配置的可信离线引擎二进制，
/// 路径与参数校验不等同于对任意第三方外部二进制的操作系统级网络沙箱。
/// </summary>
public sealed class LocalOfflineSttProvider : ISttProvider
{
	/// <summary>默认本地离线提供商标识。</summary>
	public const string DefaultProviderId = "local_offline";

	/// <summary>配置键：本地识别引擎类型。</summary>
	public const string EngineKindConfigKey = "local_stt_engine";

	/// <summary>配置键：本地模型文件或目录路径。</summary>
	public const string ModelPathConfigKey = "local_stt_model_path";

	/// <summary>配置键：本地推理线程数。</summary>
	public const string ThreadsConfigKey = "local_stt_threads";

	/// <summary>配置键：是否启用本地硬件加速。</summary>
	public const string HardwareAccelerationConfigKey = "local_stt_gpu";

	private static readonly SttProviderCapabilities UnavailableCapabilities = new()
	{
		SupportsBatch = false,
		SupportsRealtimePcm = false,
		SupportsIncrementalDelta = false,
		SupportsLanguageHint = false,
		SupportsLocalOffline = true,
		SupportsHardwareAcceleration = false,
		RequiredSampleRate = 16000,
		RequiredChannels = 1,
	};

	private readonly ILocalSttEngine? _engine;
	private readonly Func<LocalSttModelConfig?>? _configFactory;

	/// <summary>使用指定的本地引擎与配置提供器构造离线识别提供商。</summary>
	public LocalOfflineSttProvider(
		ILocalSttEngine? engine = null,
		Func<LocalSttModelConfig?>? configFactory = null)
	{
		_engine = engine;
		_configFactory = configFactory;
	}

	/// <summary>从 <see cref="ConfigStore"/> 读取本地离线配置并构造离线识别提供商。</summary>
	public LocalOfflineSttProvider(ConfigStore config, ILocalSttEngine? engine = null)
	{
		ArgumentNullException.ThrowIfNull(config);
		_engine = engine;
		_configFactory = () => ReadFromConfig(config, engine?.EngineKind ?? LocalSttEngineKind.WhisperCpp);
	}

	/// <inheritdoc />
	public string ProviderId => DefaultProviderId;

	/// <inheritdoc />
	public SttMode Mode => SttMode.Local;

	/// <inheritdoc />
	public SttProviderCapabilities Capabilities => _engine is null
		? UnavailableCapabilities
		: _engine.Capabilities;

	/// <inheritdoc />
	public Task<string> TranscribeAsync(
		RecordedAudio audio,
		SttRequestOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(options);
		cancellationToken.ThrowIfCancellationRequested();

		RecordedAudio validated = AudioMime.ValidateRecorded(audio.Bytes, audio.Mime, audio.FileName);
		ILocalSttEngine engine = RequireAvailableEngine();
		if (!Capabilities.SupportsBatch)
		{
			throw new NotSupportedException("当前本地离线语音识别引擎不支持整段录音转写");
		}

		LocalSttModelConfig effectiveConfig = ResolveAndValidateModelConfig(engine.EngineKind, options);
		return engine.TranscribeLocalAsync(validated, effectiveConfig, cancellationToken);
	}

	/// <inheritdoc />
	public Task<IStreamingSttSession> OpenStreamingSessionAsync(
		SttRequestOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		cancellationToken.ThrowIfCancellationRequested();

		ILocalSttEngine engine = RequireAvailableEngine();
		if (!Capabilities.SupportsRealtimePcm)
		{
			throw new NotSupportedException("当前本地离线语音识别引擎不支持实时流式识别，请改用整段离线转写");
		}

		LocalSttModelConfig effectiveConfig = ResolveAndValidateModelConfig(engine.EngineKind, options);
		return engine.OpenLocalStreamAsync(effectiveConfig, cancellationToken);
	}

	internal static LocalSttModelConfig ReadFromConfig(ConfigStore config, LocalSttEngineKind fallbackKind)
	{
		string rawKind = config.GetStringOr(EngineKindConfigKey, string.Empty).Trim().ToLowerInvariant();
		LocalSttEngineKind kind = rawKind switch
		{
			"whispercpp" or "whisper.cpp" or "whisper_cpp" => LocalSttEngineKind.WhisperCpp,
			"sherpaonnx" or "sherpa-onnx" or "sherpa_onnx" => LocalSttEngineKind.SherpaOnnx,
			"custom" => LocalSttEngineKind.Custom,
			_ => fallbackKind,
		};

		string modelPath = config.GetStringOr(ModelPathConfigKey, string.Empty).Trim();
		int threads = (int)Math.Clamp(
			config.GetClampedDouble(ThreadsConfigKey, LocalSttModelConfig.DefaultThreads, 1, 64),
			1,
			64);
		bool useGpu = config.GetBoolOr(HardwareAccelerationConfigKey, false);
		string? language = config.GetStringOr("realtime_voice_language", "zh").Trim();
		if (language.Length == 0) language = null;

		return new LocalSttModelConfig
		{
			EngineKind = kind,
			ModelPath = modelPath,
			Threads = threads,
			UseHardwareAcceleration = useGpu,
			Language = language,
		};
	}

	internal static string ValidateModelPathSyntax(
		string? rawPath,
		Func<string, string?>? linkTargetResolver = null)
	{
		if (string.IsNullOrWhiteSpace(rawPath))
		{
			throw new InvalidOperationException("未配置本地离线语音识别模型路径，请手动下载模型并在语音设置中指定本地模型文件路径");
		}

		string trimmed = rawPath.Trim();
		foreach (char character in trimmed)
		{
			if (char.IsControl(character))
			{
				throw new InvalidOperationException("本地离线语音模型路径包含无效控制字符");
			}
		}

		if (IsUncOrNetworkPath(trimmed))
		{
			throw new InvalidOperationException("本地离线语音模型路径不允许使用 UNC、网络共享或 URI 路径");
		}

		string[] segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		foreach (string segment in segments)
		{
			if (segment == "..")
			{
				throw new InvalidOperationException("本地离线语音模型路径不允许包含相对上级目录跳转 (..)");
			}
		}

		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(trimmed);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new InvalidOperationException("本地离线语音模型路径格式无效，请重新选择有效的本地模型文件或目录", exception);
		}

		if (IsUncOrNetworkPath(fullPath))
		{
			throw new InvalidOperationException("本地离线语音模型路径不允许使用 UNC、网络共享或 URI 路径");
		}

		EnsureNoUncOrNetworkSymlinkTarget(fullPath, "本地离线语音模型路径", linkTargetResolver);
		return fullPath;
	}

	internal static bool IsUncOrNetworkPath(string? rawPath)
	{
		if (string.IsNullOrWhiteSpace(rawPath)) return false;
		string trimmed = rawPath.Trim();

		if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
			|| trimmed.Contains("://", StringComparison.Ordinal))
		{
			return true;
		}

		string slashNormalized = trimmed.Replace('\\', '/');
		if (slashNormalized.StartsWith("//", StringComparison.Ordinal))
		{
			return true;
		}

		bool isWindowsDrivePath = trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';
		if (!isWindowsDrivePath
			&& Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
			&& (uri.IsUnc || !uri.IsFile))
		{
			return true;
		}

		return false;
	}

	internal static bool HasUncOrNetworkSymlinkTarget(
		string? path,
		Func<string, string?>? linkTargetResolver = null)
	{
		if (string.IsNullOrWhiteSpace(path) || IsUncOrNetworkPath(path)) return false;
		return FindUncOrNetworkSymlinkTarget(path, linkTargetResolver) is not null;
	}

	internal static void EnsureNoUncOrNetworkSymlinkTarget(
		string path,
		string subject,
		Func<string, string?>? linkTargetResolver = null)
	{
		if (FindUncOrNetworkSymlinkTarget(path, linkTargetResolver) is not null)
		{
			throw new InvalidOperationException($"{subject}包含指向 UNC 或网络共享的符号链接，已拒绝访问");
		}
	}

	private static string? FindUncOrNetworkSymlinkTarget(
		string path,
		Func<string, string?>? linkTargetResolver)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}

		string? root = Path.GetPathRoot(fullPath);
		if (string.IsNullOrEmpty(root))
		{
			return CheckSingleSegmentForUncSymlink(fullPath, linkTargetResolver);
		}

		string? rootTarget = CheckSingleSegmentForUncSymlink(root, linkTargetResolver);
		if (rootTarget is not null) return rootTarget;

		string relative = Path.GetRelativePath(root, fullPath);
		if (relative == ".") return null;

		string current = root;
		foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment is "." or "..") continue;
			current = Path.Combine(current, segment);
			string? target = CheckSingleSegmentForUncSymlink(current, linkTargetResolver);
			if (target is not null) return target;
		}

		return null;
	}

	private static string? CheckSingleSegmentForUncSymlink(
		string segmentPath,
		Func<string, string?>? linkTargetResolver)
	{
		if (linkTargetResolver is not null)
		{
			string? injectedTarget = linkTargetResolver(segmentPath);
			if (IsUncOrNetworkPath(injectedTarget))
			{
				return injectedTarget;
			}
		}

		try
		{
			FileInfo fileProbe = new(segmentPath);
			string? directTarget = fileProbe.LinkTarget;
			if (IsUncOrNetworkPath(directTarget))
			{
				return directTarget;
			}

			FileAttributes attributes = File.GetAttributes(segmentPath);
			if ((attributes & FileAttributes.ReparsePoint) == 0)
			{
				return null;
			}

			FileSystemInfo current = (attributes & FileAttributes.Directory) != 0
				? new DirectoryInfo(segmentPath)
				: fileProbe;

			const int maxHops = 32;
			for (int hop = 0; hop < maxHops; hop++)
			{
				string? rawTarget = current.LinkTarget;
				if (IsUncOrNetworkPath(rawTarget))
				{
					return rawTarget;
				}

				if (IsUncOrNetworkPath(current.FullName))
				{
					return current.FullName;
				}

				FileSystemInfo? next = current.ResolveLinkTarget(returnFinalTarget: false);
				if (next is null)
				{
					break;
				}

				if (IsUncOrNetworkPath(next.FullName) || IsUncOrNetworkPath(next.LinkTarget))
				{
					return IsUncOrNetworkPath(next.LinkTarget) ? next.LinkTarget : next.FullName;
				}

				current = next;
			}
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
		{
			// 路径不存在或链接目标不可读时，留待后续本地文件存在性检查处理。
		}

		return null;
	}

	private ILocalSttEngine RequireAvailableEngine()
	{
		if (_engine is null)
		{
			throw new InvalidOperationException("未安装或未启用本地离线语音识别引擎，请先安装离线语音扩展并手动配置本地模型路径");
		}

		if (!_engine.Capabilities.SupportsLocalOffline)
		{
			throw new InvalidOperationException("当前语音识别引擎未声明支持本地离线运行 (SupportsLocalOffline = false)，已拒绝作为本地离线提供商执行");
		}

		if (!_engine.IsAvailable)
		{
			throw new InvalidOperationException("未安装或未启用本地离线语音识别引擎，请先安装离线语音扩展并手动配置本地模型路径");
		}

		return _engine;
	}

	private LocalSttModelConfig ResolveAndValidateModelConfig(LocalSttEngineKind engineKind, SttRequestOptions options)
	{
		LocalSttModelConfig baseConfig = _configFactory?.Invoke() ?? new LocalSttModelConfig
		{
			EngineKind = engineKind,
		};

		string rawModelPath = !string.IsNullOrWhiteSpace(baseConfig.ModelPath)
			? baseConfig.ModelPath
			: options.Model ?? string.Empty;

		string canonicalPath = ValidateModelPathSyntax(rawModelPath);

		string? language = Capabilities.SupportsLanguageHint
			? (!string.IsNullOrWhiteSpace(options.Language) ? options.Language.Trim() : baseConfig.Language)
			: null;

		int threads = Math.Clamp(baseConfig.Threads, 1, 64);
		TimeSpan timeout = baseConfig.Timeout > TimeSpan.Zero
			? baseConfig.Timeout
			: LocalSttModelConfig.DefaultTimeout;

		return baseConfig with
		{
			ModelPath = canonicalPath,
			Threads = threads,
			Language = string.IsNullOrWhiteSpace(language) ? null : language,
			Timeout = timeout,
		};
	}
}

/// <summary>受控本地进程执行结果。</summary>
public readonly record struct LocalSttProcessOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>本地离线识别临时音频文件清理失败记录。</summary>
public sealed record LocalSttTempCleanupFailure(
	string FilePath,
	string Message,
	Exception Exception,
	DateTimeOffset OccurredAtUtc);

/// <summary>
/// 受控外部本地进程 STT 引擎基类。
///
/// 严格通过 <see cref="ProcessStartInfo.ArgumentList"/> 传递参数数组，禁止 shell 拼接；
/// 对可执行程序路径与模型路径在进入任何文件系统探测前拒绝 UNC/网络共享路径及指向 UNC/网络路径的符号链接；
/// 在取消或超时时先终止进程树并有界等待子进程退出释放文件句柄，再尽最大努力覆写并删除临时音频文件
/// （注：用户态覆写属于尽力清理，在 SSD 磨损均衡或 CoW 文件系统上不构成物理扇区不可恢复销毁保证）；
/// 若最终删除失败，通过可观测回调与状态记录暴露而不静默吞掉。
/// 安全边界说明：宿主侧本地离线适配器仅运行用户显式配置的可信离线引擎二进制，宿主自身不发起任何网络请求，
/// 但路径与参数校验不等同于对任意第三方外部二进制的操作系统级网络沙箱。
/// </summary>
public abstract class ProcessLocalSttEngine : ILocalSttEngine
{
	private const int WipeBufferBytes = 64 * 1024;
	private static readonly TimeSpan ProcessTerminationWaitTimeout = TimeSpan.FromSeconds(3);

	private readonly string _executablePath;
	private readonly Func<string, bool> _fileExists;
	private readonly Func<string, bool> _directoryExists;
	private readonly Func<ProcessStartInfo, CancellationToken, Task<LocalSttProcessOutput>> _processRunner;
	private readonly Func<string, string?>? _linkTargetResolver;
	private readonly Action<LocalSttTempCleanupFailure>? _onTempFileCleanupFailed;
	private readonly Action<string>? _beforeTempFileDelete;
	private readonly Action<string> _deleteFile;
	private LocalSttTempCleanupFailure? _lastCleanupFailure;

	/// <summary>构造外部本地进程 STT 引擎。</summary>
	protected ProcessLocalSttEngine(
		string executablePath,
		LocalSttEngineKind engineKind,
		SttProviderCapabilities capabilities)
		: this(executablePath, engineKind, capabilities, null, null, null, null, null, null, null)
	{
	}

	internal ProcessLocalSttEngine(
		string executablePath,
		LocalSttEngineKind engineKind,
		SttProviderCapabilities capabilities,
		Func<string, bool>? fileExists = null,
		Func<string, bool>? directoryExists = null,
		Func<ProcessStartInfo, CancellationToken, Task<LocalSttProcessOutput>>? processRunner = null,
		Func<string, string?>? linkTargetResolver = null,
		Action<LocalSttTempCleanupFailure>? onTempFileCleanupFailed = null,
		Action<string>? beforeTempFileDelete = null,
		Action<string>? deleteFile = null)
	{
		ArgumentNullException.ThrowIfNull(capabilities);
		_executablePath = executablePath?.Trim() ?? string.Empty;
		EngineKind = engineKind;
		Capabilities = capabilities;
		_fileExists = fileExists ?? File.Exists;
		_directoryExists = directoryExists ?? Directory.Exists;
		_processRunner = processRunner ?? RunProcessDefaultAsync;
		_linkTargetResolver = linkTargetResolver;
		_onTempFileCleanupFailed = onTempFileCleanupFailed;
		_beforeTempFileDelete = beforeTempFileDelete;
		_deleteFile = deleteFile ?? File.Delete;
	}

	/// <summary>当临时音频文件最终删除失败时触发（用于可观测告警与诊断，不静默吞掉错误）。</summary>
	public event Action<LocalSttTempCleanupFailure>? TempFileCleanupFailed;

	/// <summary>最近一次临时音频文件清理失败记录；无失败时为 null。</summary>
	public LocalSttTempCleanupFailure? LastCleanupFailure => Volatile.Read(ref _lastCleanupFailure);

	/// <summary>已配置的可执行文件路径。</summary>
	public string ExecutablePath => _executablePath;

	/// <inheritdoc />
	public LocalSttEngineKind EngineKind { get; }

	/// <inheritdoc />
	public SttProviderCapabilities Capabilities { get; }

	/// <inheritdoc />
	public virtual bool IsAvailable =>
		IsSafeExecutablePath(_executablePath)
		&& !LocalOfflineSttProvider.HasUncOrNetworkSymlinkTarget(_executablePath, _linkTargetResolver)
		&& _fileExists(_executablePath);

	/// <inheritdoc />
	public async Task<string> TranscribeLocalAsync(
		RecordedAudio audio,
		LocalSttModelConfig config,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(config);
		cancellationToken.ThrowIfCancellationRequested();

		RecordedAudio validated = AudioMime.ValidateRecorded(audio.Bytes, audio.Mime, audio.FileName);
		if (!Capabilities.SupportsLocalOffline)
		{
			throw new InvalidOperationException("当前本地进程引擎未声明支持本地离线运行 (SupportsLocalOffline = false)，已拒绝执行");
		}

		ValidateExecutablePath(_executablePath);
		if (!IsAvailable)
		{
			throw new InvalidOperationException("本地离线语音识别可执行程序不存在或路径无效，请检查离线语音扩展安装状态");
		}

		string canonicalModelPath = ValidateExistingModelPath(config.ModelPath);
		TimeSpan timeout = config.Timeout > TimeSpan.Zero ? config.Timeout : LocalSttModelConfig.DefaultTimeout;
		string extension = Path.GetExtension(AudioMime.FileNameFor(validated.Mime));
		if (string.IsNullOrEmpty(extension)) extension = ".wav";
		string tempAudioPath = Path.Combine(Path.GetTempPath(), $"nori-local-stt-{Guid.NewGuid():N}{extension}");

		try
		{
			await WriteRestrictedTempFileAsync(tempAudioPath, validated.Bytes, cancellationToken).ConfigureAwait(false);

			IReadOnlyList<string> arguments = BuildArguments(canonicalModelPath, tempAudioPath, config);
			ProcessStartInfo startInfo = BuildStartInfo(_executablePath, canonicalModelPath, arguments);

			using CancellationTokenSource timeoutCts = new(timeout);
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutCts.Token);

			LocalSttProcessOutput result;
			try
			{
				result = await _processRunner(startInfo, linkedCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				throw new VoiceProviderException(
					LocalOfflineSttProvider.DefaultProviderId,
					VoiceFailureKind.Timeout,
					"本地离线语音识别超时，请缩短单次录音或调整模型配置");
			}

			if (result.ExitCode != 0)
			{
				string detail = SttSanitizer.RedactError(result.StandardError);
				string message = detail.Length > 0
					? $"本地离线语音识别进程执行失败 (退出码 {result.ExitCode}): {detail}"
					: $"本地离线语音识别进程执行失败 (退出码 {result.ExitCode})";
				throw new InvalidOperationException(message);
			}

			return ParseTranscript(result.StandardOutput);
		}
		finally
		{
			BestEffortWipeAndDeleteFile(tempAudioPath);
		}
	}

	/// <inheritdoc />
	public virtual Task<IStreamingSttSession> OpenLocalStreamAsync(
		LocalSttModelConfig config,
		CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("当前本地离线进程引擎仅支持整段音频转写，不支持实时流式会话");

	/// <summary>构建传给本地识别进程的有序参数列表（禁止经由 shell 解析）。</summary>
	protected abstract IReadOnlyList<string> BuildArguments(
		string canonicalModelPath,
		string tempAudioPath,
		LocalSttModelConfig config);

	/// <summary>从标准输出解析识别文本。</summary>
	protected virtual string ParseTranscript(string standardOutput) =>
		string.IsNullOrWhiteSpace(standardOutput) ? string.Empty : standardOutput.Trim();

	private void ValidateExecutablePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("本地离线语音识别可执行程序不存在或路径无效，请检查离线语音扩展安装状态");
		}

		if (LocalOfflineSttProvider.IsUncOrNetworkPath(path))
		{
			throw new InvalidOperationException("本地离线语音识别可执行程序路径不允许使用 UNC、网络共享或 URI 路径");
		}

		if (!IsSafeExecutablePath(path))
		{
			throw new InvalidOperationException("本地离线语音识别可执行程序路径无效，必须为安全的本地绝对路径");
		}

		LocalOfflineSttProvider.EnsureNoUncOrNetworkSymlinkTarget(
			Path.GetFullPath(path),
			"本地离线语音识别可执行程序路径",
			_linkTargetResolver);
	}

	private string ValidateExistingModelPath(string? rawPath)
	{
		string canonicalPath = LocalOfflineSttProvider.ValidateModelPathSyntax(rawPath, _linkTargetResolver);
		if (!_fileExists(canonicalPath) && !_directoryExists(canonicalPath))
		{
			throw new InvalidOperationException("指定的本地离线语音模型路径不存在，请检查并重新配置本地模型文件路径");
		}
		return canonicalPath;
	}

	private static bool IsSafeExecutablePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return false;
		foreach (char character in path)
		{
			if (char.IsControl(character)) return false;
		}

		if (LocalOfflineSttProvider.IsUncOrNetworkPath(path)) return false;

		string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		foreach (string segment in segments)
		{
			if (segment == "..") return false;
		}

		if (!Path.IsPathFullyQualified(path)) return false;

		try
		{
			return !LocalOfflineSttProvider.IsUncOrNetworkPath(Path.GetFullPath(path));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static ProcessStartInfo BuildStartInfo(
		string executablePath,
		string canonicalModelPath,
		IReadOnlyList<string> arguments)
	{
		string? workingDir = Path.GetDirectoryName(canonicalModelPath);
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WorkingDirectory = !string.IsNullOrEmpty(workingDir) ? workingDir : Path.GetTempPath(),
		};

		foreach (string argument in arguments)
		{
			if (argument is null || argument.Contains('\0', StringComparison.Ordinal))
			{
				throw new InvalidOperationException("本地离线语音引擎参数包含非法字符");
			}
			startInfo.ArgumentList.Add(argument);
		}

		return startInfo;
	}

	private static async Task WriteRestrictedTempFileAsync(
		string path,
		byte[] bytes,
		CancellationToken cancellationToken)
	{
		FileStreamOptions streamOptions = new()
		{
			Mode = FileMode.CreateNew,
			Access = FileAccess.Write,
			Share = FileShare.None,
			Options = FileOptions.Asynchronous,
		};
		if (!OperatingSystem.IsWindows())
		{
			streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		await using FileStream stream = new(path, streamOptions);
		await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<LocalSttProcessOutput> RunProcessDefaultAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken)
	{
		using Process process = Process.Start(startInfo) // nosemgrep
			?? throw new InvalidOperationException("无法启动本地离线语音识别进程");

		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			await TerminateAndWaitForExitAsync(process, ProcessTerminationWaitTimeout).ConfigureAwait(false);
			await ObserveStreamTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);
			throw;
		}

		string stdout = await stdoutTask.ConfigureAwait(false);
		string stderr = await stderrTask.ConfigureAwait(false);
		return new LocalSttProcessOutput(process.ExitCode, stdout, stderr);
	}

	private static async Task TerminateAndWaitForExitAsync(Process process, TimeSpan waitTimeout)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// 进程已退出或平台不支持进程树终止，仍尝试等待进程退出释放文件句柄。
		}

		try
		{
			if (!process.HasExited)
			{
				using CancellationTokenSource waitCts = new(waitTimeout);
				await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
		{
			// 有界等待超时或进程句柄已释放，交由后续清理阶段尝试删除并暴露可能的占用失败。
		}
	}

	private static async Task ObserveStreamTasksAsync(Task<string> stdoutTask, Task<string> stderrTask)
	{
		try
		{
			await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
		}
		catch
		{
			// 进程取消后输出管道读取任务随进程终止而取消或关闭，此处仅防止未观察任务异常。
		}
	}

	private void BestEffortWipeAndDeleteFile(string path)
	{
		Exception? wipeException = null;
		try
		{
			if (File.Exists(path))
			{
				long length = new FileInfo(path).Length;
				if (length > 0)
				{
					byte[] zeroBuffer = ArrayPool<byte>.Shared.Rent(WipeBufferBytes);
					try
					{
						Array.Clear(zeroBuffer);
						using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None);
						long remaining = length;
						while (remaining > 0)
						{
							int writeSize = (int)Math.Min(zeroBuffer.Length, remaining);
							stream.Write(zeroBuffer, 0, writeSize);
							remaining -= writeSize;
						}
						stream.Flush(flushToDisk: true);
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(zeroBuffer);
					}
				}

				_beforeTempFileDelete?.Invoke(path);
			}
		}
		catch (Exception exception)
		{
			// 尽最大努力覆写；记录覆写阶段异常但仍继续执行删除，避免临时录音残留在磁盘。
			wipeException = exception;
		}

		try
		{
			if (File.Exists(path))
			{
				_deleteFile(path);
			}
		}
		catch (Exception deleteException)
		{
			Exception combined = wipeException is null
				? deleteException
				: new IOException(
					$"临时音频文件覆写与删除均失败: {deleteException.Message}",
					new AggregateException(wipeException, deleteException));
			RecordCleanupFailure(path, combined);
			return;
		}

		if (File.Exists(path))
		{
			RecordCleanupFailure(
				path,
				new IOException($"临时音频文件删除后仍存在于磁盘: {path}", wipeException));
		}
	}

	private void RecordCleanupFailure(string path, Exception exception)
	{
		string detail = SttSanitizer.RedactError(exception.Message);
		string message = detail.Length > 0
			? $"临时音频文件清理失败 ({Path.GetFileName(path)}): {detail}"
			: $"临时音频文件清理失败 ({Path.GetFileName(path)})";
		LocalSttTempCleanupFailure failure = new(path, message, exception, DateTimeOffset.UtcNow);
		Volatile.Write(ref _lastCleanupFailure, failure);
		_onTempFileCleanupFailed?.Invoke(failure);
		TempFileCleanupFailed?.Invoke(failure);
	}
}

/// <summary>基于 whisper.cpp CLI 的受控本地离线语音识别适配器。</summary>
public sealed class WhisperCppProcessEngine : ProcessLocalSttEngine
{
	private static readonly SttProviderCapabilities DefaultCapabilities = new()
	{
		SupportsBatch = true,
		SupportsRealtimePcm = false,
		SupportsIncrementalDelta = false,
		SupportsLanguageHint = true,
		SupportsLocalOffline = true,
		SupportsHardwareAcceleration = true,
		RequiredSampleRate = 16000,
		RequiredChannels = 1,
	};

	/// <summary>构造 whisper.cpp 本地进程识别适配器。</summary>
	public WhisperCppProcessEngine(string executablePath)
		: base(executablePath, LocalSttEngineKind.WhisperCpp, DefaultCapabilities)
	{
	}

	internal WhisperCppProcessEngine(
		string executablePath,
		Func<string, bool>? fileExists,
		Func<string, bool>? directoryExists,
		Func<ProcessStartInfo, CancellationToken, Task<LocalSttProcessOutput>>? processRunner,
		Func<string, string?>? linkTargetResolver = null,
		Action<LocalSttTempCleanupFailure>? onTempFileCleanupFailed = null,
		Action<string>? beforeTempFileDelete = null,
		Action<string>? deleteFile = null)
		: base(
			executablePath,
			LocalSttEngineKind.WhisperCpp,
			DefaultCapabilities,
			fileExists,
			directoryExists,
			processRunner,
			linkTargetResolver,
			onTempFileCleanupFailed,
			beforeTempFileDelete,
			deleteFile)
	{
	}

	/// <inheritdoc />
	protected override IReadOnlyList<string> BuildArguments(
		string canonicalModelPath,
		string tempAudioPath,
		LocalSttModelConfig config)
	{
		List<string> args =
		[
			"-m",
			canonicalModelPath,
			"-f",
			tempAudioPath,
			"-t",
			Math.Clamp(config.Threads, 1, 64).ToString(System.Globalization.CultureInfo.InvariantCulture),
			"-nt",
			"--no-prints",
		];

		if (!string.IsNullOrWhiteSpace(config.Language))
		{
			args.Add("-l");
			args.Add(config.Language.Trim());
		}

		if (!config.UseHardwareAcceleration)
		{
			args.Add("-ng");
		}

		return args;
	}
}
