using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;

namespace Nori.Core.Voice;

/// <summary>
/// 优云智算 Modelverse IndexTTS-2 适配器 (OpenAI 兼容 /v1/audio/speech)。
///
/// 与 OpenAI 兼容端点不同，IndexTTS-2 有自己默认的 Base URL 与模型名，
/// 音色为 uspeech:xxxx (经参考音频克隆得到)，原生返回 WAV。
/// 扩展字段 (情感/采样率/增益/分块静音) 通过配置键可选透传。
///
/// 音色克隆：模板音频上传后本地存档到 data/resources/indextts/voices/，
/// 缓存 voice_id 与上传时间；7 天过期后用存档音频自动重新上传续期。
/// </summary>
public sealed class IndexTtsProvider(HttpClient httpClient, ConfigStore config, AppStoragePaths? paths = null) : ITtsProvider
{
	private const string DefaultBaseUrl = "https://api.modelverse.cn/v1";
	private const string DefaultModel = "IndexTeam/IndexTTS-2";
	private const double DefaultEmotionAlpha = 0.3;
	private const string DefaultTemplateAudioConfigKey = "indextts_template_audio";
	private const string SpeechEndpointSuffix = "/audio/speech";

	/// <summary>音色有效期 (秒), 与平台 7 天一致。</summary>
	internal static readonly TimeSpan VoiceTtl = TimeSpan.FromDays(7);

	private readonly AppStoragePaths _paths = paths ?? new AppStoragePaths(Environment.CurrentDirectory);

	public string Name => "indextts";

	public async Task<EncodedAudio> SynthesizeAsync(
		string text,
		TtsSynthesizeOptions options,
		CancellationToken cancellationToken)
	{
		string apiKey = config.GetStringOr("tts_api_key", "").Trim();
		// 配置了模板音频时，音色由模板解析决定；options.Voice 已带 uspeech 前缀时
		// 视为已解析结果直接使用（SynthesizeCoreAsync 已解析过，避免重复）。
		string templatePath = config.GetStringOr(DefaultTemplateAudioConfigKey, "").Trim();
		string voice = templatePath.Length > 0
			? options.Voice is {Length: > 0} requested && requested.Trim().StartsWith("uspeech:", StringComparison.OrdinalIgnoreCase)
				? requested.Trim()
				: await ResolveTemplateVoiceAsync(cancellationToken)
			: options.Voice is {Length: > 0} requested2
				? requested2.Trim()
				: "";
		if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未配置 IndexTTS-2 API Key");
		if (voice.Length == 0) throw new InvalidOperationException("未配置 IndexTTS-2 音色：请在设置中提供音频模板文件并上传克隆");

		JsonObject payload = new()
		{
			["model"] = ResolveModel(),
			["input"] = text,
			["voice"] = voice,
		};
		if (options.Speed is > 0)
		{
			payload["speed"] = options.Speed;
		}
		// IndexTTS-2 可选扩展字段：只透传用户显式配置过的值。
		AppendOptional(payload, config, "indextts_sample_rate", "sample_rate");
		AppendOptional(payload, config, "indextts_gain", "gain");
		AppendOptional(payload, config, "indextts_interval_silence", "interval_silence");
		AppendEmotion(payload, config, options.EmotionText);

		using HttpRequestMessage request = new(HttpMethod.Post, ResolveEndpoint())
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			string error = await ReadErrorAsync(response.Content, cancellationToken);
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"IndexTTS-2 请求失败: HTTP {(int)response.StatusCode} {error}",
				httpStatusCode: (int)response.StatusCode);
		}
		return await VoiceHttpContent.ReadAudioAsync(response.Content, cancellationToken);
	}

	private string ResolveModel()
	{
		string model = config.GetStringOr("tts_model", DefaultModel).Trim();
		return model.Length == 0 ? DefaultModel : model;
	}

	private string ResolveApiBaseUrl()
	{
		string baseUrl = (config.GetStringOr("tts_base_url", "") is {Length: > 0} saved ? saved : DefaultBaseUrl)
			.Trim().TrimEnd('/');
		return baseUrl.EndsWith(SpeechEndpointSuffix, StringComparison.OrdinalIgnoreCase)
			? baseUrl[..^SpeechEndpointSuffix.Length]
			: baseUrl;
	}

	private string ResolveEndpoint() => $"{ResolveApiBaseUrl()}{SpeechEndpointSuffix}";

	/// <summary>返回会影响 IndexTTS 合成结果、需要参与合成缓存身份的配置。</summary>
	internal string GetSynthesisCacheVariant()
	{
		double emotionAlpha = ReadDouble(config, "indextts_emo_alpha", DefaultEmotionAlpha);
		return $"emo_alpha={emotionAlpha.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
	}

	// ---- 模板音频 → 音色 (上传/存档/缓存/续期) ----

	/// <summary>
	/// 从配置的音频模板文件解析出可用的 voice_id。
	///
	/// 模板音频首次使用会克隆并本地存档 + 写缓存；7 天过期后用存档自动续期，
	/// 全程无感。未配置模板音频时返回空串。
	/// </summary>
	public async Task<string> ResolveTemplateVoiceAsync(CancellationToken cancellationToken)
	{
		string templatePath = config.GetStringOr(DefaultTemplateAudioConfigKey, "").Trim();
		if (templatePath.Length == 0) return "";

		IndexTtsVoiceCache cache = LoadCache();
		IndexTtsVoiceEntry? entry = cache.FindEntryBySource(templatePath);
		if (entry is not null)
		{
			if (!string.IsNullOrWhiteSpace(entry.VoiceId) && !cache.IsExpired(templatePath))
			{
				return entry.VoiceId;
			}

			// 已缓存音色续期时优先读取应用自己的存档；原始模板即使被移动、删除或位于离线盘也不影响续期。
			if (!string.IsNullOrWhiteSpace(entry.ArchiveFile) && File.Exists(entry.ArchiveFile))
			{
				return await UploadAndCacheVoiceAsync(
					templatePath,
					entry.ArchiveFile,
					string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileNameWithoutExtension(templatePath) : entry.Name,
					cache,
					cancellationToken);
			}
		}

		if (!File.Exists(templatePath))
		{
			throw new InvalidOperationException($"IndexTTS-2 音频模板文件不存在，且没有可用的本地存档: {templatePath}");
		}

		return await CloneVoiceAsync(templatePath, cache, cancellationToken);
	}

	/// <summary>公开上传接口：由设置 UI 的「上传克隆」按钮调用，返回克隆的 voice_id。</summary>
	public async Task<string> CloneVoiceAsync(string templatePath, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(templatePath)) throw new InvalidOperationException("音频模板文件路径不能为空");
		if (!File.Exists(templatePath)) throw new InvalidOperationException($"音频模板文件不存在: {templatePath}");
		IndexTtsVoiceCache cache = LoadCache();
		return await CloneVoiceAsync(templatePath, cache, cancellationToken);
	}

	private async Task<string> CloneVoiceAsync(string templatePath, IndexTtsVoiceCache cache, CancellationToken cancellationToken)
	{
		string archiveFile = ArchiveTemplateAudio(templatePath);
		string name = Path.GetFileNameWithoutExtension(templatePath);
		return await UploadAndCacheVoiceAsync(templatePath, archiveFile, name, cache, cancellationToken);
	}

	private async Task<string> UploadAndCacheVoiceAsync(
		string sourcePath,
		string archiveFile,
		string name,
		IndexTtsVoiceCache cache,
		CancellationToken cancellationToken)
	{
		string apiKey = config.GetStringOr("tts_api_key", "").Trim();
		if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未配置 IndexTTS-2 API Key");

		string voiceId = await UploadVoiceAsync(archiveFile, name, apiKey, cancellationToken);
		cache.Set(sourcePath, voiceId, archiveFile, name, DateTimeOffset.UtcNow);
		SaveCache(cache);
		return voiceId;
	}

	/// <summary>把模板音频复制到存档目录，按内容 hash 命名去重。</summary>
	private string ArchiveTemplateAudio(string templatePath)
	{
		Directory.CreateDirectory(_paths.IndexTtsVoicesDirectory);
		byte[] content = File.ReadAllBytes(templatePath);
		string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
		string extension = Path.GetExtension(templatePath);
		if (extension.Length == 0) extension = ".wav";
		string archiveFile = Path.Combine(_paths.IndexTtsVoicesDirectory, $"template-{hash}{extension}");
		if (!File.Exists(archiveFile))
		{
			File.Copy(templatePath, archiveFile);
		}
		return archiveFile;
	}

	private async Task<string> UploadVoiceAsync(string audioFile, string name, string apiKey, CancellationToken cancellationToken)
	{
		string url = $"{ResolveApiBaseUrl()}/audio/voice/upload";

		using MultipartFormDataContent form = new();
		byte[] audioBytes = await File.ReadAllBytesAsync(audioFile, cancellationToken);
		ByteArrayContent file = new(audioBytes);
		file.Headers.TryAddWithoutValidation("Content-Type", InferAudioMime(audioFile));
		form.Add(file, "speaker_file", Path.GetFileName(audioFile));
		form.Add(new StringContent(name), "name");
		form.Add(new StringContent(ResolveModel()), "model");

		using HttpRequestMessage request = new(HttpMethod.Post, url) {Content = form};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		byte[] responseBytes = await VoiceHttpContent.ReadBytesAsync(response.Content, cancellationToken, allowEmpty: true);
		if (!response.IsSuccessStatusCode)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"IndexTTS-2 音色克隆失败: HTTP {(int)response.StatusCode} {ReadUploadError(responseBytes)}",
				httpStatusCode: (int)response.StatusCode);
		}

		try
		{
			JsonNode? body = JsonNode.Parse(Encoding.UTF8.GetString(responseBytes));
			string? voiceId = body?["id"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(voiceId))
			{
				throw new VoiceProviderException(Name, VoiceFailureKind.InvalidResponse, "IndexTTS-2 克隆响应缺少 id");
			}
			return voiceId;
		}
		catch (JsonException exception)
		{
			throw new VoiceProviderException(Name, VoiceFailureKind.InvalidResponse, "IndexTTS-2 克隆响应不是合法 JSON", exception);
		}
	}

	private static string InferAudioMime(string path)
	{
		string extension = Path.GetExtension(path).ToLowerInvariant();
		return extension switch
		{
			".mp3" => "audio/mpeg",
			".wav" => "audio/wav",
			".ogg" => "audio/ogg",
			".flac" => "audio/flac",
			_ => "application/octet-stream",
		};
	}

	private static string ReadUploadError(byte[] bytes)
	{
		if (bytes.Length == 0) return "";
		string raw = Encoding.UTF8.GetString(bytes);
		try
		{
			JsonNode? body = JsonNode.Parse(raw);
			string? message = body?["error"]?["message"]?.GetValue<string>();
			if (!string.IsNullOrWhiteSpace(message)) return message;
		}
		catch (JsonException) { }
		return raw;
	}

	private IndexTtsVoiceCache LoadCache()
	{
		if (!File.Exists(_paths.IndexTtsCachePath)) return new IndexTtsVoiceCache();
		try
		{
			string json = File.ReadAllText(_paths.IndexTtsCachePath);
			return JsonSerializer.Deserialize<IndexTtsVoiceCache>(json) ?? new IndexTtsVoiceCache();
		}
		catch (JsonException)
		{
			return new IndexTtsVoiceCache();
		}
	}

	private void SaveCache(IndexTtsVoiceCache cache)
	{
		Directory.CreateDirectory(_paths.IndexTtsDirectory);
		string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions {WriteIndented = true});
		File.WriteAllText(_paths.IndexTtsCachePath, json);
	}

	/// <summary>
	/// IndexTTS-2 音色缓存：源路径 → voice_id / 存档文件 / 上传时间。
	/// </summary>
	public sealed class IndexTtsVoiceCache
	{
		/// <summary>按源路径（用户原始模板路径）索引的条目。</summary>
		/// <remarks>Windows 路径大小写不敏感，统一用忽略大小写比较，避免同一文件不同大小写重复克隆。</remarks>
		public Dictionary<string, IndexTtsVoiceEntry> Voices { get; set; } = new(PathKeyComparer);

		/// <summary>按源路径查找缓存条目。</summary>
		public IndexTtsVoiceEntry? FindEntryBySource(string sourcePath)
		{
			string key = NormalizeKey(sourcePath);
			return Voices.TryGetValue(key, out IndexTtsVoiceEntry? entry) ? entry : null;
		}

		/// <summary>按源路径查找未过期的 voice_id。</summary>
		public string? FindBySource(string sourcePath)
		{
			IndexTtsVoiceEntry? entry = FindEntryBySource(sourcePath);
			return entry is null || string.IsNullOrWhiteSpace(entry.VoiceId) ? null : entry.VoiceId;
		}

		/// <summary>判断源路径对应音色是否已过期。</summary>
		public bool IsExpired(string sourcePath)
		{
			IndexTtsVoiceEntry? entry = FindEntryBySource(sourcePath);
			if (entry is null) return true;
			long uploaded = entry.UploadUnixSeconds;
			if (uploaded <= 0) return true;
			return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(uploaded) > VoiceTtl;
		}

		/// <summary>写入/覆盖缓存条目（key 始终是源路径，续期更新同一把 key，避免漂移）。</summary>
		public void Set(string sourcePath, string voiceId, string archiveFile, string name, DateTimeOffset uploaded)
		{
			Voices[NormalizeKey(sourcePath)] = new IndexTtsVoiceEntry
			{
				VoiceId = voiceId,
				ArchiveFile = archiveFile,
				Name = name,
				UploadUnixSeconds = uploaded.ToUnixTimeSeconds(),
			};
		}

		/// <summary>路径比较器：Windows 路径大小写不敏感，其他平台区分大小写。</summary>
		private static readonly StringComparer PathKeyComparer =
			OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

		/// <summary>规范化缓存 key：解析相对路径并统一 Windows 路径大小写。</summary>
		private static string NormalizeKey(string path)
		{
			string full = Path.GetFullPath(path.Trim());
			return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
		}
	}

	/// <summary>缓存条目。</summary>
	public sealed class IndexTtsVoiceEntry
	{
		public string VoiceId { get; set; } = "";
		public string ArchiveFile { get; set; } = "";
		public string Name { get; set; } = "";
		public long UploadUnixSeconds { get; set; }
	}

	/// <summary>
	/// 注入情感控制参数 (IndexTTS-2 文本情感法, method 3)。
	///
	/// 情绪来源优先 options.EmotionText, 其次配置键 indextts_emo_text;
	/// neutral / 空值时不传 emo 字段 (由音色自带的参考情绪决定, 最自然)。
	/// 强度默认 0.3, 可经 indextts_emo_alpha 配置覆盖。
	/// </summary>
	private static void AppendEmotion(JsonObject payload, ConfigStore config, string? emotionText)
	{
		string? emotion = string.IsNullOrWhiteSpace(emotionText)
			? config.GetStringOr("indextts_emo_text", "").Trim() is {Length: > 0} configured ? configured : null
			: emotionText.Trim();
		if (string.IsNullOrWhiteSpace(emotion)) return;
		if (emotion.Equals(EmotionTextNeutral, StringComparison.OrdinalIgnoreCase)) return;

		payload["emo_control_method"] = 3;
		payload["emo_text"] = emotion;
		payload["emo_weight"] = ReadDouble(config, "indextts_emo_alpha", DefaultEmotionAlpha);
	}

	/// <summary>neutral 情绪值 (系统情绪状态机使用, 语义为无情绪倾向)。</summary>
	private const string EmotionTextNeutral = "neutral";

	/// <summary>读取数值配置, 非法或缺失时回退默认值。</summary>
	private static double ReadDouble(ConfigStore config, string key, double fallback)
	{
		string raw = config.GetStringOr(key, "");
		// 配置值读取时会把 0/1 推断为布尔, 这里还原为数值以保留边界值.
		if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
		if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
		return double.TryParse(raw, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 1 ? value : fallback;
	}

	/// <summary>从配置读取可选扩展字段；配置值存在且非空时才加入 payload。</summary>
	private static void AppendOptional(JsonObject payload, ConfigStore config, string configKey, string apiField)
	{
		string value = config.GetStringOr(configKey, "").Trim();
		if (value.Length == 0) return;
		if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int intValue))
		{
			payload[apiField] = intValue;
		}
		else if (double.TryParse(value, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
		{
			payload[apiField] = doubleValue;
		}
		else
		{
			payload[apiField] = value;
		}
	}

	private static async Task<string> ReadErrorAsync(HttpContent content, CancellationToken cancellationToken)
	{
		try
		{
			byte[] bytes = await VoiceHttpContent.ReadBytesAsync(content, cancellationToken, allowEmpty: true);
			if (bytes.Length == 0) return "";
			string raw = Encoding.UTF8.GetString(bytes);
			try
			{
				JsonNode? body = JsonNode.Parse(raw);
				string? message = body?["error"]?["message"]?.GetValue<string>();
				if (!string.IsNullOrWhiteSpace(message)) return message;
			}
			catch (JsonException) { }
			return raw;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return "";
		}
	}
}
