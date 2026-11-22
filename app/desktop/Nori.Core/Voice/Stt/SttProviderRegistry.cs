using Nori.Core.Configuration;

namespace Nori.Core.Voice.Stt;

/// <summary>语音识别提供商的能力、网络与隐私元数据描述符。</summary>
public sealed record SttProviderDescriptor
{
	/// <summary>提供商稳定标识。</summary>
	public required string ProviderId { get; init; }

	/// <summary>向界面与诊断展示的名称。</summary>
	public required string DisplayName { get; init; }

	/// <summary>运行模式。</summary>
	public required SttMode Mode { get; init; }

	/// <summary>能力声明。</summary>
	public required SttProviderCapabilities Capabilities { get; init; }

	/// <summary>是否依赖网络请求。</summary>
	public bool RequiresNetwork { get; init; }

	/// <summary>是否将用户音频上传至外部或云端端点。</summary>
	public bool UploadsAudioToCloud { get; init; }

	/// <summary>面向用户的隐私说明。</summary>
	public string PrivacyNotice { get; init; } = string.Empty;
}

/// <summary>已注册的语音识别提供商及其能力描述符。</summary>
public sealed record SttRegisteredProvider(
	SttProviderDescriptor Descriptor,
	ISttProvider Provider);

/// <summary>按能力、隐私与离线约束筛选 STT 提供商的查询条件。</summary>
public sealed record SttSelectionCriteria
{
	/// <summary>是否要求支持增量 delta 文本输出。</summary>
	public bool RequireIncrementalDelta { get; init; }

	/// <summary>是否要求支持实时 PCM 流式输入。</summary>
	public bool RequireRealtimePcm { get; init; }

	/// <summary>是否要求支持整段录音转写。</summary>
	public bool RequireBatch { get; init; }

	/// <summary>是否要求支持本地离线运行。</summary>
	public bool RequireLocalOffline { get; init; }

	/// <summary>是否要求支持语言提示。</summary>
	public bool RequireLanguageHint { get; init; }

	/// <summary>是否要求支持硬件加速。</summary>
	public bool RequireHardwareAcceleration { get; init; }

	/// <summary>是否允许使用依赖网络的提供商；为 false 时仅匹配本地离线提供商。</summary>
	public bool AllowNetwork { get; init; } = true;

	/// <summary>是否允许将音频上传到外部云端端点。</summary>
	public bool AllowCloudAudioUpload { get; init; } = true;

	/// <summary>期望优先匹配的运行模式（可选）。</summary>
	public SttMode? PreferredMode { get; init; }

	/// <summary>同能力条件下的可选首选提供商标识（仅用于同分并列决策，不覆盖能力筛选）。</summary>
	public string? PreferredProviderId { get; init; }
}

/// <summary>
/// 语音识别提供商能力注册表。
///
/// 统一管理提供商注册、替换、列举与基于 <see cref="SttProviderCapabilities"/> 的筛选路由，
/// 彻底避免按服务商名称字符串硬编码能力判断。
/// </summary>
public sealed class SttProviderRegistry
{
	private readonly Lock _gate = new();
	private readonly List<SttRegisteredProvider> _entries = [];

	/// <summary>创建内置默认注册表，注册云端流式、云端整段及本地离线提供商。</summary>
	public static SttProviderRegistry CreateDefault(
		HttpClient httpClient,
		ConfigStore config,
		ILocalSttEngine? localEngine = null,
		Func<LocalSttModelConfig?>? localConfigFactory = null,
		Func<IRealtimeWebSocketTransport>? realtimeTransportFactory = null,
		bool includeCloudProviders = true)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(config);

		SttProviderRegistry registry = new();

		if (includeCloudProviders)
		{
			OpenAiRealtimeSttProvider realtime = new(config, realtimeTransportFactory);
			registry.Register(realtime, new SttProviderDescriptor
			{
				ProviderId = realtime.ProviderId,
				DisplayName = "OpenAI 实时语音识别 (Realtime)",
				Mode = realtime.Mode,
				Capabilities = realtime.Capabilities,
				RequiresNetwork = true,
				UploadsAudioToCloud = true,
				PrivacyNotice = "实时语音流将通过加密 WebSocket 发送至配置的云端 STT 服务端进行流式转写。",
			});

			WhisperSttProvider whisper = new(httpClient, config);
			GptTranscribeProvider gptTranscribe = new(httpClient, config, whisper);

			registry.Register(gptTranscribe, new SttProviderDescriptor
			{
				ProviderId = gptTranscribe.ProviderId,
				DisplayName = "OpenAI 整段语音转写 (GPT Transcribe)",
				Mode = gptTranscribe.Mode,
				Capabilities = gptTranscribe.Capabilities,
				RequiresNetwork = true,
				UploadsAudioToCloud = true,
				PrivacyNotice = "完整录音片段将在说话结束后上传至配置的云端转写端点。",
			});

			registry.Register(whisper, new SttProviderDescriptor
			{
				ProviderId = whisper.ProviderId,
				DisplayName = "OpenAI Whisper 兼容转写",
				Mode = whisper.Mode,
				Capabilities = whisper.Capabilities,
				RequiresNetwork = true,
				UploadsAudioToCloud = true,
				PrivacyNotice = "完整录音片段将在说话结束后上传至配置的兼容 Whisper 端点。",
			});
		}

		LocalOfflineSttProvider local = localConfigFactory is not null
			? new LocalOfflineSttProvider(localEngine, localConfigFactory)
			: new LocalOfflineSttProvider(config, localEngine);

		registry.Register(local, new SttProviderDescriptor
		{
			ProviderId = local.ProviderId,
			DisplayName = "本地离线语音识别",
			Mode = local.Mode,
			Capabilities = local.Capabilities,
			RequiresNetwork = false,
			UploadsAudioToCloud = false,
			PrivacyNotice = "音频仅在本机离线引擎中处理，不发起任何网络请求，也不上传云端。",
		});

		return registry;
	}

	/// <summary>注册或替换一个 STT 提供商及其能力描述符。</summary>
	public void Register(ISttProvider provider, SttProviderDescriptor? descriptor = null)
	{
		ArgumentNullException.ThrowIfNull(provider);
		if (string.IsNullOrWhiteSpace(provider.ProviderId))
		{
			throw new ArgumentException("语音识别提供商标识不能为空", nameof(provider));
		}

		if (provider.Mode == SttMode.Local && !provider.Capabilities.SupportsLocalOffline)
		{
			throw new ArgumentException("本地模式语音识别提供商必须声明支持本地离线运行 (SupportsLocalOffline = true)", nameof(provider));
		}

		SttProviderDescriptor resolvedDescriptor = descriptor is null
			? BuildDefaultDescriptor(provider)
			: NormalizeDescriptor(provider, descriptor);

		lock (_gate)
		{
			int existingIndex = FindIndexById(provider.ProviderId);
			SttRegisteredProvider entry = new(resolvedDescriptor, provider);
			if (existingIndex >= 0)
			{
				_entries[existingIndex] = entry;
			}
			else
			{
				_entries.Add(entry);
			}
		}
	}

	/// <summary>移除指定标识的提供商。</summary>
	public bool Remove(string providerId)
	{
		if (string.IsNullOrWhiteSpace(providerId)) return false;
		lock (_gate)
		{
			int index = FindIndexById(providerId);
			if (index < 0) return false;
			_entries.RemoveAt(index);
			return true;
		}
	}

	/// <summary>按标识查找已注册提供商。</summary>
	public bool TryGet(string providerId, out SttRegisteredProvider? registered)
	{
		if (string.IsNullOrWhiteSpace(providerId))
		{
			registered = null;
			return false;
		}

		lock (_gate)
		{
			int index = FindIndexById(providerId);
			if (index < 0)
			{
				registered = null;
				return false;
			}
			registered = SyncEntry(_entries[index]);
			return true;
		}
	}

	/// <summary>按标识获取已注册提供商，不存在时抛出清晰中文异常。</summary>
	public SttRegisteredProvider GetRequired(string providerId)
	{
		if (!TryGet(providerId, out SttRegisteredProvider? registered) || registered is null)
		{
			throw new InvalidOperationException($"未找到标识为 {providerId} 的语音识别提供商");
		}
		return registered;
	}

	/// <summary>列举当前已注册的所有提供商及其最新能力描述符。</summary>
	public IReadOnlyList<SttRegisteredProvider> List()
	{
		lock (_gate)
		{
			List<SttRegisteredProvider> snapshot = new(_entries.Count);
			foreach (SttRegisteredProvider entry in _entries)
			{
				snapshot.Add(SyncEntry(entry));
			}
			return snapshot;
		}
	}

	/// <summary>按能力与隐私条件筛选所有匹配的提供商，并按匹配度降序排列。</summary>
	public IReadOnlyList<SttRegisteredProvider> FindMatching(SttSelectionCriteria criteria)
	{
		ArgumentNullException.ThrowIfNull(criteria);

		List<SttRegisteredProvider> snapshot = [.. List()];
		List<(SttRegisteredProvider Entry, int Score, int Order)> matched = [];

		for (int index = 0; index < snapshot.Count; index++)
		{
			SttRegisteredProvider entry = snapshot[index];
			if (!Matches(entry, criteria)) continue;
			matched.Add((entry, Score(entry, criteria), index));
		}

		matched.Sort(static (left, right) =>
		{
			int scoreOrder = right.Score.CompareTo(left.Score);
			return scoreOrder != 0 ? scoreOrder : left.Order.CompareTo(right.Order);
		});

		List<SttRegisteredProvider> result = new(matched.Count);
		foreach ((SttRegisteredProvider entry, _, _) in matched)
		{
			result.Add(entry);
		}
		return result;
	}

	/// <summary>按能力条件选择最佳提供商；无匹配项时返回 null。</summary>
	public SttRegisteredProvider? SelectBest(SttSelectionCriteria criteria)
	{
		IReadOnlyList<SttRegisteredProvider> matches = FindMatching(criteria);
		return matches.Count > 0 ? matches[0] : null;
	}

	/// <summary>按能力条件选择最佳 <see cref="ISttProvider"/> 实例；无匹配项时返回 null。</summary>
	public ISttProvider? SelectBestProvider(SttSelectionCriteria criteria) =>
		SelectBest(criteria)?.Provider;

	private int FindIndexById(string providerId)
	{
		for (int index = 0; index < _entries.Count; index++)
		{
			if (string.Equals(_entries[index].Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
			{
				return index;
			}
		}
		return -1;
	}

	private static SttRegisteredProvider SyncEntry(SttRegisteredProvider entry) =>
		entry with
		{
			Descriptor = entry.Descriptor with
			{
				Mode = entry.Provider.Mode,
				Capabilities = entry.Provider.Capabilities,
			},
		};

	private static SttProviderDescriptor BuildDefaultDescriptor(ISttProvider provider)
	{
		bool localOfflineOnly = provider.Mode == SttMode.Local && provider.Capabilities.SupportsLocalOffline;
		return new SttProviderDescriptor
		{
			ProviderId = provider.ProviderId,
			DisplayName = provider.ProviderId,
			Mode = provider.Mode,
			Capabilities = provider.Capabilities,
			RequiresNetwork = !localOfflineOnly,
			UploadsAudioToCloud = !localOfflineOnly,
			PrivacyNotice = localOfflineOnly
				? "音频仅在本机离线处理，不发起网络请求。"
				: "音频将发送至外部语音识别服务进行处理。",
		};
	}

	private static SttProviderDescriptor NormalizeDescriptor(ISttProvider provider, SttProviderDescriptor descriptor)
	{
		if (!string.Equals(provider.ProviderId, descriptor.ProviderId, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("描述符 ProviderId 必须与提供商实例一致", nameof(descriptor));
		}

		if (provider.Mode != descriptor.Mode)
		{
			throw new ArgumentException("描述符 Mode 必须与提供商运行模式一致", nameof(descriptor));
		}

		if (provider.Mode == SttMode.Local
			&& provider.Capabilities.SupportsLocalOffline
			&& (descriptor.RequiresNetwork || descriptor.UploadsAudioToCloud))
		{
			throw new ArgumentException("声明本地离线的语音识别提供商不得依赖网络或上传音频至云端", nameof(descriptor));
		}

		return descriptor with
		{
			ProviderId = provider.ProviderId,
			Mode = provider.Mode,
			Capabilities = provider.Capabilities,
		};
	}

	private static bool Matches(SttRegisteredProvider entry, SttSelectionCriteria criteria)
	{
		SttProviderCapabilities capabilities = entry.Provider.Capabilities;
		SttProviderDescriptor descriptor = entry.Descriptor;

		if (criteria.RequireIncrementalDelta && !capabilities.SupportsIncrementalDelta) return false;
		if (criteria.RequireRealtimePcm && !capabilities.SupportsRealtimePcm) return false;
		if (criteria.RequireBatch && !capabilities.SupportsBatch) return false;
		if (criteria.RequireLocalOffline && !capabilities.SupportsLocalOffline) return false;
		if (criteria.RequireLanguageHint && !capabilities.SupportsLanguageHint) return false;
		if (criteria.RequireHardwareAcceleration && !capabilities.SupportsHardwareAcceleration) return false;

		if (!criteria.AllowNetwork
			&& (entry.Provider.Mode != SttMode.Local
				|| !capabilities.SupportsLocalOffline
				|| descriptor.RequiresNetwork
				|| descriptor.UploadsAudioToCloud))
		{
			return false;
		}

		if (!criteria.AllowCloudAudioUpload && descriptor.UploadsAudioToCloud)
		{
			return false;
		}

		return true;
	}

	private static int Score(SttRegisteredProvider entry, SttSelectionCriteria criteria)
	{
		SttProviderCapabilities capabilities = entry.Provider.Capabilities;
		SttProviderDescriptor descriptor = entry.Descriptor;
		int score = 0;

		if (criteria.PreferredMode is { } preferredMode && entry.Provider.Mode == preferredMode)
		{
			score += 1000;
		}

		if ((!criteria.AllowNetwork || criteria.RequireLocalOffline)
			&& entry.Provider.Mode == SttMode.Local
			&& capabilities.SupportsLocalOffline
			&& !descriptor.RequiresNetwork
			&& !descriptor.UploadsAudioToCloud)
		{
			score += 500;
		}

		if ((criteria.RequireRealtimePcm || criteria.RequireIncrementalDelta)
			&& capabilities.SupportsRealtimePcm
			&& capabilities.SupportsIncrementalDelta)
		{
			score += 200;
		}

		if (criteria.RequireBatch && capabilities.SupportsBatch)
		{
			score += 200;
		}

		if (criteria.RequireHardwareAcceleration && capabilities.SupportsHardwareAcceleration)
		{
			score += 100;
		}

		if (!string.IsNullOrWhiteSpace(criteria.PreferredProviderId)
			&& string.Equals(descriptor.ProviderId, criteria.PreferredProviderId.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			score += 50;
		}

		if (capabilities.SupportsLanguageHint) score += 20;
		if (!descriptor.UploadsAudioToCloud) score += 10;

		return score;
	}
}
