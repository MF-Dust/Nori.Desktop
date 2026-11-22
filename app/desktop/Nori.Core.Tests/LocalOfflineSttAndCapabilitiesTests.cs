using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Voice;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

/// <summary>SttProviderRegistry 能力路由与 LocalOfflineSttProvider 本地离线扩展接缝测试。</summary>
public sealed class LocalOfflineSttAndCapabilitiesTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-local-stt-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly List<string> _tempFiles = [];

	public LocalOfflineSttAndCapabilitiesTests()
	{
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	[Fact]
	public void SttProviderRegistry按能力路由且不依赖名称硬编码()
	{
		using HttpClient httpClient = new(new StubHttpHandler());
		FakeLocalEngine offlineBatchEngine = new(
			LocalSttEngineKind.WhisperCpp,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = false,
				SupportsIncrementalDelta = false,
				SupportsLanguageHint = true,
				SupportsLocalOffline = true,
				SupportsHardwareAcceleration = true,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			});

		SttProviderRegistry registry = SttProviderRegistry.CreateDefault(
			httpClient,
			_config,
			offlineBatchEngine,
			() => new LocalSttModelConfig { ModelPath = "/models/ggml-small.bin" });

		// 1. 流式增量：只选择支持实时 PCM 与增量 delta 的提供商
		SttRegisteredProvider? streaming = registry.SelectBest(new SttSelectionCriteria
		{
			RequireRealtimePcm = true,
			RequireIncrementalDelta = true,
			AllowNetwork = true,
		});
		Assert.NotNull(streaming);
		Assert.Equal("openai_realtime", streaming.Descriptor.ProviderId);
		Assert.True(streaming.Descriptor.RequiresNetwork);
		Assert.True(streaming.Descriptor.UploadsAudioToCloud);

		// 2. 整段转写：仅凭能力选择批量识别；PreferredProviderId 仅在同分时并列决胜
		SttRegisteredProvider? batchDefault = registry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = true,
			PreferredMode = SttMode.BatchCloud,
		});
		Assert.NotNull(batchDefault);
		Assert.Equal("gpt_transcribe", batchDefault.Descriptor.ProviderId);

		SttRegisteredProvider? batchPreferredWhisper = registry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = true,
			PreferredMode = SttMode.BatchCloud,
			PreferredProviderId = "whisper",
		});
		Assert.NotNull(batchPreferredWhisper);
		Assert.Equal("whisper", batchPreferredWhisper.Descriptor.ProviderId);

		// 3. 硬件加速 + 本地离线筛选：匹配到支持硬件加速的本地提供商
		SttRegisteredProvider? gpuLocal = registry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			RequireLocalOffline = true,
			RequireHardwareAcceleration = true,
			AllowNetwork = false,
		});
		Assert.NotNull(gpuLocal);
		Assert.Equal(LocalOfflineSttProvider.DefaultProviderId, gpuLocal.Descriptor.ProviderId);
		Assert.False(gpuLocal.Descriptor.RequiresNetwork);
		Assert.False(gpuLocal.Descriptor.UploadsAudioToCloud);

		// 4. 禁网 / Safe Mode：即使首选指定云端标识，也绝不允许匹配需要网络的提供商
		SttRegisteredProvider? safeModeMatch = registry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = false,
			PreferredProviderId = "gpt_transcribe",
		});
		Assert.NotNull(safeModeMatch);
		Assert.Equal(LocalOfflineSttProvider.DefaultProviderId, safeModeMatch.Descriptor.ProviderId);

		// 5. 禁网下要求实时流式，而当前本地引擎不支持流式时返回 null（不降级到云端）
		SttRegisteredProvider? offlineStreaming = registry.SelectBest(new SttSelectionCriteria
		{
			RequireRealtimePcm = true,
			RequireIncrementalDelta = true,
			AllowNetwork = false,
		});
		Assert.Null(offlineStreaming);

		// 6. 零名称硬编码验证：注册任意自定义名称的流式本地提供商并要求禁网，可按能力直接路由命中
		ArbitraryNameProvider customOfflineStream = new(
			"my_custom_edge_asr_v9",
			SttMode.Local,
			new SttProviderCapabilities
			{
				SupportsBatch = false,
				SupportsRealtimePcm = true,
				SupportsIncrementalDelta = true,
				SupportsLanguageHint = true,
				SupportsLocalOffline = true,
				SupportsHardwareAcceleration = false,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			});
		registry.Register(customOfflineStream);

		SttRegisteredProvider? customHit = registry.SelectBest(new SttSelectionCriteria
		{
			RequireRealtimePcm = true,
			RequireIncrementalDelta = true,
			AllowNetwork = false,
		});
		Assert.NotNull(customHit);
		Assert.Equal("my_custom_edge_asr_v9", customHit.Descriptor.ProviderId);
	}

	[Fact]
	public void SttProviderRegistry在云端禁用或禁止上传音频时过滤并拒绝契约矛盾的提供商()
	{
		using HttpClient httpClient = new(new StubHttpHandler());
		FakeLocalEngine engine = new(
			LocalSttEngineKind.SherpaOnnx,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = true,
				SupportsIncrementalDelta = true,
				SupportsLanguageHint = false,
				SupportsLocalOffline = true,
				SupportsHardwareAcceleration = false,
			});

		// includeCloudProviders: false（安全模式场景）仅注册本地提供商
		SttProviderRegistry safeRegistry = SttProviderRegistry.CreateDefault(
			httpClient,
			_config,
			engine,
			includeCloudProviders: false);
		SttRegisteredProvider single = Assert.Single(safeRegistry.List());
		Assert.Equal(LocalOfflineSttProvider.DefaultProviderId, single.Descriptor.ProviderId);

		// 1. 注册校验反例：声明 Mode == Local 且 SupportsLocalOffline == true，但 RequiresNetwork == true 或 UploadsAudioToCloud == true 时必须拒绝
		Assert.Throws<ArgumentException>(() => safeRegistry.Register(
			new ArbitraryNameProvider(
				"contradictory_local_network",
				SttMode.Local,
				new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true }),
			new SttProviderDescriptor
			{
				ProviderId = "contradictory_local_network",
				DisplayName = "矛盾的本地联网引擎",
				Mode = SttMode.Local,
				Capabilities = new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true },
				RequiresNetwork = true,
				UploadsAudioToCloud = false,
			}));

		Assert.Throws<ArgumentException>(() => safeRegistry.Register(
			new ArbitraryNameProvider(
				"contradictory_local_upload",
				SttMode.Local,
				new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true }),
			new SttProviderDescriptor
			{
				ProviderId = "contradictory_local_upload",
				DisplayName = "矛盾的本地上传引擎",
				Mode = SttMode.Local,
				Capabilities = new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true },
				RequiresNetwork = false,
				UploadsAudioToCloud = true,
			}));

		// 2. 注册校验反例：声明 Mode == Local 但 SupportsLocalOffline == false 时必须拒绝注册
		Assert.Throws<ArgumentException>(() => safeRegistry.Register(
			new ArbitraryNameProvider(
				"local_without_offline_capability",
				SttMode.Local,
				new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = false })));

		// 3. 筛选反例：即使外部云端提供商声明无需联网或误标本地能力，只要 UploadsAudioToCloud == true 或 Mode != Local，禁网时绝不可选中
		safeRegistry.Register(
			new ArbitraryNameProvider(
				"cloud_upload_no_network_flag",
				SttMode.BatchCloud,
				new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true }),
			new SttProviderDescriptor
			{
				ProviderId = "cloud_upload_no_network_flag",
				DisplayName = "上传云端提供商",
				Mode = SttMode.BatchCloud,
				Capabilities = new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true },
				RequiresNetwork = false,
				UploadsAudioToCloud = true,
			});

		IReadOnlyList<SttRegisteredProvider> offlineOnly = safeRegistry.FindMatching(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = false,
		});
		SttRegisteredProvider match = Assert.Single(offlineOnly);
		Assert.Equal(LocalOfflineSttProvider.DefaultProviderId, match.Descriptor.ProviderId);

		SttRegisteredProvider? preferredCloudRejected = safeRegistry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = false,
			PreferredProviderId = "cloud_upload_no_network_flag",
		});
		Assert.NotNull(preferredCloudRejected);
		Assert.Equal(LocalOfflineSttProvider.DefaultProviderId, preferredCloudRejected.Descriptor.ProviderId);

		// 若注册表中仅存在 UploadsAudioToCloud == true 的提供商，禁网时 SelectBest 必须返回 null
		SttProviderRegistry uploadOnlyRegistry = new();
		uploadOnlyRegistry.Register(
			new ArbitraryNameProvider(
				"cloud_upload_only",
				SttMode.BatchCloud,
				new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true }),
			new SttProviderDescriptor
			{
				ProviderId = "cloud_upload_only",
				DisplayName = "仅云端上传",
				Mode = SttMode.BatchCloud,
				Capabilities = new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true },
				RequiresNetwork = false,
				UploadsAudioToCloud = true,
			});
		Assert.Null(uploadOnlyRegistry.SelectBest(new SttSelectionCriteria
		{
			RequireBatch = true,
			AllowNetwork = false,
		}));
	}

	[Fact]
	public async Task LocalOfflineSttProvider支持离线整段转写且严格无网络请求()
	{
		FakeLocalEngine engine = new(
			LocalSttEngineKind.WhisperCpp,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = false,
				SupportsIncrementalDelta = false,
				SupportsLanguageHint = true,
				SupportsLocalOffline = true,
				SupportsHardwareAcceleration = true,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			})
		{
			BatchResult = "你好，这是本地离线识别结果",
		};

		_config.Set(LocalOfflineSttProvider.EngineKindConfigKey, new ConfigValue.Text("whisper_cpp"));
		_config.Set(LocalOfflineSttProvider.ModelPathConfigKey, new ConfigValue.Text(Path.Combine(Path.GetTempPath(), "ggml-base.bin")));
		_config.Set(LocalOfflineSttProvider.ThreadsConfigKey, new ConfigValue.Integer(6));
		_config.Set(LocalOfflineSttProvider.HardwareAccelerationConfigKey, new ConfigValue.Boolean(true));

		LocalOfflineSttProvider provider = new(_config, engine);
		Assert.Equal(SttMode.Local, provider.Mode);
		Assert.True(provider.Capabilities.SupportsLocalOffline);
		Assert.True(provider.Capabilities.SupportsBatch);
		Assert.False(provider.Capabilities.SupportsRealtimePcm);

		RecordedAudio sample = new([0x52, 0x49, 0x46, 0x46], "audio/wav", "offline.wav");
		string text = await provider.TranscribeAsync(sample, new SttRequestOptions { Language = "zh" });

		Assert.Equal("你好，这是本地离线识别结果", text);
		Assert.NotNull(engine.LastBatchConfig);
		Assert.Equal(LocalSttEngineKind.WhisperCpp, engine.LastBatchConfig.EngineKind);
		Assert.Equal(6, engine.LastBatchConfig.Threads);
		Assert.True(engine.LastBatchConfig.UseHardwareAcceleration);
		Assert.Equal("zh", engine.LastBatchConfig.Language);
	}

	[Fact]
	public async Task LocalOfflineSttProvider未安装引擎或未配置模型路径时抛出清晰中文指引异常()
	{
		RecordedAudio sample = new([0x52, 0x49, 0x46, 0x46], "audio/wav", "sample.wav");

		// 1. 未安装本地引擎
		LocalOfflineSttProvider noEngineProvider = new(engine: null);
		Assert.True(noEngineProvider.Capabilities.SupportsLocalOffline);
		Assert.False(noEngineProvider.Capabilities.SupportsBatch);
		Assert.False(noEngineProvider.Capabilities.SupportsRealtimePcm);

		InvalidOperationException noEngineEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			noEngineProvider.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("离线语音", noEngineEx.Message, StringComparison.Ordinal);
		Assert.Contains("本地模型路径", noEngineEx.Message, StringComparison.Ordinal);

		InvalidOperationException noEngineStreamEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			noEngineProvider.OpenStreamingSessionAsync(new SttRequestOptions()));
		Assert.Contains("离线语音", noEngineStreamEx.Message, StringComparison.Ordinal);

		// 2. 引擎存在但未就绪
		FakeLocalEngine unavailableEngine = new(
			LocalSttEngineKind.SherpaOnnx,
			new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true })
		{
			IsAvailable = false,
		};
		LocalOfflineSttProvider unavailableProvider = new(unavailableEngine, () => new LocalSttModelConfig
		{
			ModelPath = "/models/sherpa",
		});
		InvalidOperationException unavailableEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			unavailableProvider.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("离线语音", unavailableEx.Message, StringComparison.Ordinal);

		// 3. 引擎就绪但模型路径为空或包含非法相对跳转
		FakeLocalEngine readyEngine = new(
			LocalSttEngineKind.WhisperCpp,
			new SttProviderCapabilities { SupportsBatch = true, SupportsLocalOffline = true });

		LocalOfflineSttProvider emptyModelProvider = new(readyEngine, () => new LocalSttModelConfig { ModelPath = "   " });
		InvalidOperationException emptyModelEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			emptyModelProvider.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("模型路径", emptyModelEx.Message, StringComparison.Ordinal);

		LocalOfflineSttProvider traversalModelProvider = new(readyEngine, () => new LocalSttModelConfig
		{
			ModelPath = "/models/../../etc/passwd",
		});
		InvalidOperationException traversalEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			traversalModelProvider.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("..", traversalEx.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LocalOfflineSttProvider如实保留底层离线声明并拒绝非离线引擎与流式越权()
	{
		// 1. 底层引擎声明 SupportsLocalOffline == false 时，Provider 不得将其改写为 true，且拒绝注册与执行
		FakeLocalEngine nonOfflineEngine = new(
			LocalSttEngineKind.Custom,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = true,
				SupportsIncrementalDelta = true,
				SupportsLocalOffline = false,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			});
		LocalOfflineSttProvider nonOfflineProvider = new(nonOfflineEngine, () => new LocalSttModelConfig
		{
			ModelPath = Path.Combine(Path.GetTempPath(), "model.bin"),
		});
		Assert.False(nonOfflineProvider.Capabilities.SupportsLocalOffline);

		SttProviderRegistry registry = new();
		Assert.Throws<ArgumentException>(() => registry.Register(nonOfflineProvider));

		RecordedAudio sample = new([0x52, 0x49, 0x46, 0x46], "audio/wav", "sample.wav");
		InvalidOperationException batchEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			nonOfflineProvider.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("离线", batchEx.Message, StringComparison.Ordinal);

		InvalidOperationException streamEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			nonOfflineProvider.OpenStreamingSessionAsync(new SttRequestOptions()));
		Assert.Contains("离线", streamEx.Message, StringComparison.Ordinal);

		// 2. 底层支持离线但不支持流式时，Capabilities.SupportsRealtimePcm 为 false，打开流式会话抛 NotSupportedException
		FakeLocalEngine batchOnlyEngine = new(
			LocalSttEngineKind.WhisperCpp,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = false,
				SupportsIncrementalDelta = false,
				SupportsLocalOffline = true,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			});
		LocalOfflineSttProvider batchOnlyProvider = new(batchOnlyEngine, () => new LocalSttModelConfig
		{
			ModelPath = Path.Combine(Path.GetTempPath(), "model.bin"),
		});
		Assert.True(batchOnlyProvider.Capabilities.SupportsLocalOffline);
		Assert.False(batchOnlyProvider.Capabilities.SupportsRealtimePcm);
		Assert.False(batchOnlyProvider.Capabilities.SupportsIncrementalDelta);
		await Assert.ThrowsAsync<NotSupportedException>(() =>
			batchOnlyProvider.OpenStreamingSessionAsync(new SttRequestOptions()));

		// 3. 底层支持流式与增量 delta 时，动态透传能力并成功打开本地流式会话
		FakeLocalEngine streamingEngine = new(
			LocalSttEngineKind.SherpaOnnx,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsRealtimePcm = true,
				SupportsIncrementalDelta = true,
				SupportsLanguageHint = true,
				SupportsLocalOffline = true,
				SupportsHardwareAcceleration = false,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			});
		LocalOfflineSttProvider streamingProvider = new(streamingEngine, () => new LocalSttModelConfig
		{
			EngineKind = LocalSttEngineKind.SherpaOnnx,
			ModelPath = Path.Combine(Path.GetTempPath(), "sherpa-paraformer"),
			Language = "zh",
		});

		Assert.True(streamingProvider.Capabilities.SupportsRealtimePcm);
		Assert.True(streamingProvider.Capabilities.SupportsIncrementalDelta);
		Assert.Equal(16000, streamingProvider.Capabilities.RequiredSampleRate);

		await using IStreamingSttSession session = await streamingProvider.OpenStreamingSessionAsync(new SttRequestOptions
		{
			Language = "en",
		});
		Assert.True(session.IsConnected);
		Assert.Equal("en", streamingEngine.LastStreamConfig?.Language);
	}

	[Fact]
	public async Task ProcessLocalSttEngine校验参数数组构造与临时录音覆写清理()
	{
		string fakeExe = CreateTempFile("fake-whisper-cli");
		string fakeModel = CreateTempFile("ggml-model.bin");
		string? capturedTempAudioPath = null;
		byte[]? observedTempBytesDuringRun = null;
		byte[]? observedTempBytesBeforeDelete = null;
		List<string>? capturedArgs = null;
		bool useShellExecute = true;

		WhisperCppProcessEngine engine = new(
			fakeExe,
			fileExists: path => path == fakeExe || path == fakeModel,
			directoryExists: _ => false,
			processRunner: (startInfo, _) =>
			{
				useShellExecute = startInfo.UseShellExecute;
				capturedArgs = [.. startInfo.ArgumentList];
				int fIndex = capturedArgs.IndexOf("-f");
				Assert.True(fIndex >= 0 && fIndex + 1 < capturedArgs.Count);
				capturedTempAudioPath = capturedArgs[fIndex + 1];
				observedTempBytesDuringRun = File.ReadAllBytes(capturedTempAudioPath);
				return Task.FromResult(new LocalSttProcessOutput(0, " 离线测试文本 \n", ""));
			},
			beforeTempFileDelete: path => observedTempBytesBeforeDelete = File.ReadAllBytes(path));

		RecordedAudio audio = new([0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4], "audio/wav", "input.wav");
		LocalSttModelConfig config = new()
		{
			EngineKind = LocalSttEngineKind.WhisperCpp,
			ModelPath = fakeModel,
			Threads = 3,
			UseHardwareAcceleration = false,
			Language = "zh;rm -rf /",
		};

		string result = await engine.TranscribeLocalAsync(audio, config);
		Assert.Equal("离线测试文本", result);
		Assert.False(useShellExecute);
		Assert.NotNull(capturedArgs);
		Assert.Equal(audio.Bytes, observedTempBytesDuringRun);
		Assert.NotNull(observedTempBytesBeforeDelete);
		Assert.Equal(audio.Bytes.Length, observedTempBytesBeforeDelete.Length);
		Assert.All(observedTempBytesBeforeDelete, value => Assert.Equal(0, value));
		Assert.Null(engine.LastCleanupFailure);
		// 语言参数作为单独 ArgumentList 元素传递，不经过 shell 解释
		Assert.Contains("zh;rm -rf /", capturedArgs);
		Assert.Contains("-ng", capturedArgs);
		// 完成后临时音频文件必须被彻底删除
		Assert.NotNull(capturedTempAudioPath);
		Assert.False(File.Exists(capturedTempAudioPath));

		// 取消或超时异常时同样必须执行零值覆写并删除临时音频文件
		string? cancelledTempPath = null;
		byte[]? cancelledBytesBeforeDelete = null;
		WhisperCppProcessEngine cancellingEngine = new(
			fakeExe,
			fileExists: path => path == fakeExe || path == fakeModel,
			directoryExists: _ => false,
			processRunner: async (startInfo, token) =>
			{
				List<string> args = [.. startInfo.ArgumentList];
				cancelledTempPath = args[args.IndexOf("-f") + 1];
				Assert.True(File.Exists(cancelledTempPath));
				await Task.Delay(TimeSpan.FromSeconds(5), token);
				return new LocalSttProcessOutput(0, "", "");
			},
			beforeTempFileDelete: path => cancelledBytesBeforeDelete = File.ReadAllBytes(path));

		await Assert.ThrowsAnyAsync<VoiceProviderException>(() =>
			cancellingEngine.TranscribeLocalAsync(audio, config with { Timeout = TimeSpan.FromMilliseconds(30) }, CancellationToken.None));
		Assert.NotNull(cancelledTempPath);
		Assert.False(File.Exists(cancelledTempPath));
		Assert.NotNull(cancelledBytesBeforeDelete);
		Assert.Equal(audio.Bytes.Length, cancelledBytesBeforeDelete.Length);
		Assert.All(cancelledBytesBeforeDelete, value => Assert.Equal(0, value));

		// 用户主动取消返回 OperationCanceledException 且仍覆写清理临时文件
		cancelledBytesBeforeDelete = null;
		using CancellationTokenSource userCts = new();
		userCts.CancelAfter(TimeSpan.FromMilliseconds(20));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			cancellingEngine.TranscribeLocalAsync(audio, config, userCts.Token));
		Assert.False(File.Exists(cancelledTempPath));
		Assert.NotNull(cancelledBytesBeforeDelete);
		Assert.All(cancelledBytesBeforeDelete, value => Assert.Equal(0, value));
	}

	[Theory]
	[InlineData(@"\\server\share\whisper.exe")]
	[InlineData("//server/share/whisper.exe")]
	[InlineData(@"/\server\share\whisper.exe")]
	[InlineData(@"\/server/share/whisper.exe")]
	[InlineData(@"\\?\UNC\server\share\whisper.exe")]
	[InlineData(@"\\.\UNC\server\share\whisper.exe")]
	[InlineData("file://server/share/whisper.exe")]
	[InlineData("file:///C:/models/whisper.exe")]
	public async Task ProcessLocalSttEngine在任何文件系统探测前拒绝UNC与网络路径(string uncOrNetworkPath)
	{
		string validExe = CreateTempFile("valid-whisper-cli");
		string validModel = CreateTempFile("valid-model.bin");
		int probeCount = 0;

		// 1. 可执行程序为 UNC/网络路径：IsAvailable 与 TranscribeLocalAsync 均不得触碰 fileExists
		WhisperCppProcessEngine badExeEngine = new(
			uncOrNetworkPath,
			fileExists: _ =>
			{
				probeCount++;
				return true;
			},
			directoryExists: _ =>
			{
				probeCount++;
				return true;
			},
			processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")));

		Assert.False(badExeEngine.IsAvailable);
		Assert.Equal(0, probeCount);

		RecordedAudio audio = new([0x52, 0x49, 0x46, 0x46, 1, 2], "audio/wav", "test.wav");
		InvalidOperationException exeEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			badExeEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = validModel }));
		Assert.Contains("UNC", exeEx.Message, StringComparison.Ordinal);
		Assert.Equal(0, probeCount);

		// 2. 模型路径为 UNC/网络路径：不得触碰 fileExists 或 directoryExists
		WhisperCppProcessEngine badModelEngine = new(
			validExe,
			fileExists: path =>
			{
				if (path == validExe) return true;
				probeCount++;
				return true;
			},
			directoryExists: _ =>
			{
				probeCount++;
				return true;
			},
			processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")));

		InvalidOperationException modelEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			badModelEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = uncOrNetworkPath }));
		Assert.Contains("UNC", modelEx.Message, StringComparison.Ordinal);
		Assert.Equal(0, probeCount);

		// 3. LocalOfflineSttProvider 路径校验同样在任何引擎调用前拒绝 UNC/网络路径
		InvalidOperationException syntaxEx = Assert.Throws<InvalidOperationException>(() =>
			LocalOfflineSttProvider.ValidateModelPathSyntax(uncOrNetworkPath));
		Assert.Contains("UNC", syntaxEx.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProcessLocalSttEngine在文件系统探测前拒绝解析目标指向UNC的符号链接()
	{
		string validExe = CreateTempFile("valid-exe");
		string validModel = CreateTempFile("valid-model.bin");
		string symlinkedModelPath = Path.Combine(Path.GetTempPath(), $"nori-unc-link-{Guid.NewGuid():N}.bin");
		string symlinkedExePath = Path.Combine(Path.GetTempPath(), $"nori-unc-exe-link-{Guid.NewGuid():N}");
		RecordedAudio audio = new([0x52, 0x49, 0x46, 0x46, 1, 2], "audio/wav", "test.wav");

		// 1. 注入式符号链接目标解析验证（跨平台确定性，且零网络/文件系统探测）
		int modelProbeCount = 0;
		WhisperCppProcessEngine injectedModelLinkEngine = new(
			validExe,
			fileExists: path =>
			{
				if (path == validExe) return true;
				modelProbeCount++;
				return true;
			},
			directoryExists: _ =>
			{
				modelProbeCount++;
				return true;
			},
			processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")),
			linkTargetResolver: path => path == Path.GetFullPath(validModel) ? @"\\remote-nas\models\ggml.bin" : null);

		InvalidOperationException modelLinkEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			injectedModelLinkEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = validModel }));
		Assert.Contains("符号链接", modelLinkEx.Message, StringComparison.Ordinal);
		Assert.Equal(0, modelProbeCount);

		int exeProbeCount = 0;
		WhisperCppProcessEngine injectedExeLinkEngine = new(
			validExe,
			fileExists: _ =>
			{
				exeProbeCount++;
				return true;
			},
			directoryExists: _ => false,
			processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")),
			linkTargetResolver: path => path == Path.GetFullPath(validExe) ? "//remote-nas/bin/whisper" : null);

		Assert.False(injectedExeLinkEngine.IsAvailable);
		Assert.Equal(0, exeProbeCount);
		InvalidOperationException exeLinkEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			injectedExeLinkEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = validModel }));
		Assert.Contains("符号链接", exeLinkEx.Message, StringComparison.Ordinal);
		Assert.Equal(0, exeProbeCount);

		// 2. 在支持无特权创建符号链接的系统上，使用真实文件系统符号链接验证（目标指向 UNC）
		bool createdRealSymlinks = false;
		try
		{
			File.CreateSymbolicLink(symlinkedModelPath, "//server/share/model.bin");
			File.CreateSymbolicLink(symlinkedExePath, @"\\server\share\whisper.exe");
			_tempFiles.Add(symlinkedModelPath);
			_tempFiles.Add(symlinkedExePath);
			createdRealSymlinks = true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			// Windows 无开发者模式/管理员权限时允许跳过真实符号链接创建，上方注入式测试已覆盖语义。
		}

		if (createdRealSymlinks)
		{
			int realProbeCount = 0;
			WhisperCppProcessEngine realLinkEngine = new(
				validExe,
				fileExists: path =>
				{
					if (path == validExe) return true;
					realProbeCount++;
					return true;
				},
				directoryExists: _ =>
				{
					realProbeCount++;
					return true;
				},
				processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")));

			InvalidOperationException realModelEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				realLinkEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = symlinkedModelPath }));
			Assert.Contains("符号链接", realModelEx.Message, StringComparison.Ordinal);
			Assert.Equal(0, realProbeCount);

			WhisperCppProcessEngine realExeLinkEngine = new(
				symlinkedExePath,
				fileExists: _ =>
				{
					realProbeCount++;
					return true;
				},
				directoryExists: _ => false,
				processRunner: (_, _) => Task.FromResult(new LocalSttProcessOutput(0, "unexpected", "")));
			Assert.False(realExeLinkEngine.IsAvailable);
			Assert.Equal(0, realProbeCount);
		}
	}

	[Fact]
	public async Task ProcessLocalSttEngine取消真实子进程后等待退出再清理并暴露清理失败()
	{
		string fakeExe = CreateTempFile("fake-exe");
		string fakeModel = CreateTempFile("fake-model.bin");
		RecordedAudio audio = new([0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4], "audio/wav", "test.wav");

		// 1. 删除失败可观测性：不静默吞掉错误，通过事件、回调与 LastCleanupFailure 暴露，且不掩盖原始取消/超时异常
		List<LocalSttTempCleanupFailure> callbackFailures = [];
		List<LocalSttTempCleanupFailure> eventFailures = [];
		string? leakedTempFile = null;

		WhisperCppProcessEngine failingCleanupEngine = new(
			fakeExe,
			fileExists: path => path == fakeExe || path == fakeModel,
			directoryExists: _ => false,
			processRunner: async (startInfo, token) =>
			{
				List<string> args = [.. startInfo.ArgumentList];
				leakedTempFile = args[args.IndexOf("-f") + 1];
				await Task.Delay(TimeSpan.FromSeconds(5), token);
				return new LocalSttProcessOutput(0, "", "");
			},
			onTempFileCleanupFailed: callbackFailures.Add,
			deleteFile: _ => throw new IOException("模拟文件被安全软件锁定"));
		failingCleanupEngine.TempFileCleanupFailed += eventFailures.Add;

		await Assert.ThrowsAsync<VoiceProviderException>(() =>
			failingCleanupEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig
			{
				ModelPath = fakeModel,
				Timeout = TimeSpan.FromMilliseconds(25),
			}));

		Assert.NotNull(leakedTempFile);
		_tempFiles.Add(leakedTempFile);
		Assert.True(File.Exists(leakedTempFile));
		// 即使删除失败，覆写仍已完成（尽最大努力将临时录音字节清零）
		Assert.All(File.ReadAllBytes(leakedTempFile), value => Assert.Equal(0, value));
		Assert.NotNull(failingCleanupEngine.LastCleanupFailure);
		Assert.Equal(leakedTempFile, failingCleanupEngine.LastCleanupFailure.FilePath);
		Assert.Contains("清理失败", failingCleanupEngine.LastCleanupFailure.Message, StringComparison.Ordinal);
		Assert.Single(callbackFailures);
		Assert.Single(eventFailures);

		// 2. 真实子进程取消与退出等待：确认默认进程运行器在 Kill(entireProcessTree: true) 后等待退出再删除临时文件
		string shellExe = OperatingSystem.IsWindows()
			? Path.Combine(Environment.SystemDirectory, "cmd.exe")
			: "/bin/sh";
		if (File.Exists(shellExe))
		{
			string? realProcTempPath = null;
			RealShellSleepEngine realProcEngine = new(shellExe, tempPath => realProcTempPath = tempPath);
			using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(120));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				realProcEngine.TranscribeLocalAsync(audio, new LocalSttModelConfig { ModelPath = fakeModel }, cts.Token));
			Assert.NotNull(realProcTempPath);
			Assert.False(File.Exists(realProcTempPath));
			Assert.Null(realProcEngine.LastCleanupFailure);
		}
	}

	public void Dispose()
	{
		foreach (string file in _tempFiles)
		{
			try { if (File.Exists(file)) File.Delete(file); } catch { /* 测试结束尽力清理临时文件 */ }
		}
		_database.Dispose();
		try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 测试结束尽力清理临时数据库 */ }
	}

	private string CreateTempFile(string name)
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-test-{Guid.NewGuid():N}-{name}");
		File.WriteAllBytes(path, [1, 2, 3, 4]);
		_tempFiles.Add(path);
		return path;
	}

	private sealed class StubHttpHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
	}

	private sealed class FakeLocalEngine(
		LocalSttEngineKind engineKind,
		SttProviderCapabilities capabilities) : ILocalSttEngine
	{
		public LocalSttEngineKind EngineKind { get; } = engineKind;
		public SttProviderCapabilities Capabilities { get; } = capabilities;
		public bool IsAvailable { get; set; } = true;
		public string BatchResult { get; set; } = "ok";
		public LocalSttModelConfig? LastBatchConfig { get; private set; }
		public LocalSttModelConfig? LastStreamConfig { get; private set; }

		public Task<string> TranscribeLocalAsync(
			RecordedAudio audio,
			LocalSttModelConfig config,
			CancellationToken cancellationToken = default)
		{
			LastBatchConfig = config;
			return Task.FromResult(BatchResult);
		}

		public Task<IStreamingSttSession> OpenLocalStreamAsync(
			LocalSttModelConfig config,
			CancellationToken cancellationToken = default)
		{
			if (!Capabilities.SupportsRealtimePcm)
			{
				throw new NotSupportedException("引擎不支持流式识别");
			}
			LastStreamConfig = config;
			return Task.FromResult<IStreamingSttSession>(new FakeStreamingSession(LocalOfflineSttProvider.DefaultProviderId));
		}
	}

	private sealed class FakeStreamingSession(string providerId) : IStreamingSttSession
	{
		private readonly Channel<StreamingSttEvent> _channel = Channel.CreateUnbounded<StreamingSttEvent>();
		public string ProviderId { get; } = providerId;
		public bool IsConnected { get; private set; } = true;
		public ChannelReader<StreamingSttEvent> Events => _channel.Reader;

		public Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16Mono24k, CancellationToken cancellationToken = default) =>
			Task.CompletedTask;

		public Task CommitAsync(CancellationToken cancellationToken = default) =>
			Task.CompletedTask;

		public Task CloseAsync(CancellationToken cancellationToken = default)
		{
			IsConnected = false;
			_channel.Writer.TryComplete();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			IsConnected = false;
			_channel.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class ArbitraryNameProvider(
		string providerId,
		SttMode mode,
		SttProviderCapabilities capabilities) : ISttProvider
	{
		public string ProviderId { get; } = providerId;
		public SttMode Mode { get; } = mode;
		public SttProviderCapabilities Capabilities { get; } = capabilities;

		public Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken = default) =>
			Task.FromResult("custom");

		public Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken = default) =>
			Task.FromResult<IStreamingSttSession>(new FakeStreamingSession(ProviderId));
	}

	private sealed class RealShellSleepEngine(
		string shellExe,
		Action<string> onTempPathCreated) : ProcessLocalSttEngine(
			shellExe,
			LocalSttEngineKind.Custom,
			new SttProviderCapabilities
			{
				SupportsBatch = true,
				SupportsLocalOffline = true,
				RequiredSampleRate = 16000,
				RequiredChannels = 1,
			})
	{
		protected override IReadOnlyList<string> BuildArguments(
			string canonicalModelPath,
			string tempAudioPath,
			LocalSttModelConfig config)
		{
			onTempPathCreated(tempAudioPath);
			return OperatingSystem.IsWindows()
				? ["/c", "ping -n 10 127.0.0.1 >nul"]
				: ["-c", "sleep 10"];
		}
	}
}
