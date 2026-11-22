using System.Security.Cryptography;
using System.Text;

namespace Nori.Core.Voice;

/// <summary>语音请求和媒体交换共用的音频大小上限。</summary>
public static class VoiceAudioLimits
{
	/// <summary>单段音频、录音上传和 TTS 响应的最大字节数。</summary>
	public const int MaxBytes = 32 * 1024 * 1024;

	/// <summary>合成队列最多预取的音频段数。</summary>
	public const int SynthesisQueueCapacity = 2;

	/// <summary>合成缓存最多保存的条目数。</summary>
	public const int CacheItemLimit = 16;
}

/// <summary>
/// 已编码的音频数据。
///
/// MIME 是数据的一部分，不能在送入播放端时再猜测；调用方应使用
/// <see cref="AudioMime.Validate"/> 校验来自 HTTP 或 MediaRecorder 的值。
/// </summary>
public sealed record EncodedAudio(byte[] Bytes, string Mime)
{
	/// <summary>音频字节数。</summary>
	public int Length => Bytes.Length;
}

/// <summary>MediaRecorder 产生的原始录音及其格式信息。</summary>
public sealed record RecordedAudio(byte[] Bytes, string Mime, string FileName)
{
	/// <summary>录音字节数。</summary>
	public int Length => Bytes.Length;
}

/// <summary>音频 MIME 校验与录音文件名辅助。</summary>
public static class AudioMime
{
	private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		"audio/aac",
		"audio/flac",
		"audio/mp4",
		"audio/mpeg",
		"audio/mp3",
		"audio/ogg",
		"audio/opus",
		"audio/wav",
		"audio/wave",
		"audio/webm",
		"audio/x-wav",
	};

	/// <summary>
	/// 校验并规范 MIME 的主类型，同时保留 codecs 等参数。
	/// </summary>
	public static string Validate(string? mime)
	{
		if (string.IsNullOrWhiteSpace(mime)) throw new InvalidOperationException("音频 MIME 类型不能为空");
		string value = mime.Trim();
		int separator = value.IndexOf(';');
		string mediaType = (separator < 0 ? value : value[..separator]).Trim();
		if (!mediaType.Contains('/', StringComparison.Ordinal)
			|| !SupportedTypes.Contains(mediaType))
		{
			throw new InvalidOperationException($"不支持的音频 MIME 类型: {mime}");
		}

		string parameters = separator < 0 ? "" : value[separator..].Trim();
		return mediaType.ToLowerInvariant() + parameters;
	}

	/// <summary>判断 MIME 是否是受支持的音频类型。</summary>
	public static bool IsSupported(string? mime)
	{
		try
		{
			Validate(mime);
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	/// <summary>根据 MIME 选择安全的录音文件名。</summary>
	public static string FileNameFor(string mime)
	{
		string mediaType = Validate(mime).Split(';', 2)[0];
		string extension = mediaType switch
		{
			"audio/mpeg" or "audio/mp3" => "mp3",
			"audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
			"audio/ogg" or "audio/opus" => "ogg",
			"audio/mp4" => "m4a",
			"audio/aac" => "aac",
			"audio/flac" => "flac",
			_ => "webm",
		};
		return $"speech.{extension}";
	}

	/// <summary>校验音频字节、MIME 与大小，并返回不可变语义的编码对象。</summary>
	public static EncodedAudio ValidateEncoded(byte[] bytes, string? mime)
	{
		if (bytes is null || bytes.Length == 0) throw new InvalidOperationException("音频内容不能为空");
		if (bytes.Length > VoiceAudioLimits.MaxBytes) throw new InvalidOperationException("音频内容超过 32MiB 限制");
		return new EncodedAudio(bytes, Validate(mime));
	}

	/// <summary>校验录音的字节、MIME 与文件名。</summary>
	public static RecordedAudio ValidateRecorded(byte[] bytes, string? mime, string? fileName)
	{
		if (bytes is null || bytes.Length == 0) throw new InvalidOperationException("录音内容不能为空");
		if (bytes.Length > VoiceAudioLimits.MaxBytes) throw new InvalidOperationException("录音内容超过 32MiB 限制");
		string normalizedMime = Validate(mime);
		string safeName = SanitizeFileName(fileName, normalizedMime);
		return new RecordedAudio(bytes, normalizedMime, safeName);
	}

	private static string SanitizeFileName(string? fileName, string mime)
	{
		if (string.IsNullOrWhiteSpace(fileName)) return FileNameFor(mime);
		string value = fileName.Trim();
		if (value.Length > 128 || value.Any(char.IsControl)) return FileNameFor(mime);
		string name = Path.GetFileName(value);
		if (name.Length == 0 || name is "." or "..") return FileNameFor(mime);
		return name;
	}
}

/// <summary>音频 HTTP 响应读取工具，拒绝空内容和超过 32MiB 的响应。</summary>
public static class VoiceHttpContent
{
	/// <summary>读取并校验一份音频 HTTP 响应。</summary>
	public static async Task<EncodedAudio> ReadAudioAsync(HttpContent content, CancellationToken cancellationToken)
	{
		string mime = AudioMime.Validate(content.Headers.ContentType?.ToString());
		byte[] bytes = await ReadBytesAsync(content, cancellationToken);
		return AudioMime.ValidateEncoded(bytes, mime);
	}

	/// <summary>读取有大小上限的 HTTP 内容。</summary>
	public static async Task<byte[]> ReadBytesAsync(HttpContent content, CancellationToken cancellationToken, bool allowEmpty = false)
	{
		if (content.Headers.ContentLength is > VoiceAudioLimits.MaxBytes)
		{
			throw new InvalidOperationException("音频响应超过 32MiB 限制");
		}

		await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[64 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
		{
			if (buffer.Length + read > VoiceAudioLimits.MaxBytes)
			{
				throw new InvalidOperationException("音频响应超过 32MiB 限制");
			}
			buffer.Write(chunk, 0, read);
		}

		if (buffer.Length == 0 && !allowEmpty) throw new InvalidOperationException("音频响应为空");
		return buffer.ToArray();
	}
}

/// <summary>按句末标点拆分 TTS 文本，保证单段不会无限增长。</summary>
public static class SentenceChunker
{
	/// <summary>单个合成段的长度上限。</summary>
	public const int MaxChunkLength = 120;

	private static readonly HashSet<char> SentenceTerminators = ['。', '！', '？', '!', '?', '；', ';', '\n', '\r'];
	private static readonly HashSet<char> SoftTerminators = ['，', ',', '、', ':', '：'];

	/// <summary>拆分文本并去掉空段。</summary>
	public static IReadOnlyList<string> Split(string? text, int maxChunkLength = MaxChunkLength)
	{
		if (string.IsNullOrWhiteSpace(text)) return [];
		int limit = Math.Max(1, maxChunkLength);
		string normalized = text.Trim();
		List<string> result = [];
		StringBuilder current = new();

		foreach (char character in normalized)
		{
			current.Append(character);
			if (SentenceTerminators.Contains(character))
			{
				Flush(result, current);
			}
			else if (current.Length >= limit && SoftTerminators.Contains(character))
			{
				Flush(result, current);
			}
			else if (current.Length >= limit)
			{
				FlushByLimit(result, current, limit);
			}
		}
		Flush(result, current);
		return result;
	}

	private static void Flush(List<string> result, StringBuilder current)
	{
		string value = current.ToString().Trim();
		if (value.Length > 0) result.Add(value);
		current.Clear();
	}

	private static void FlushByLimit(List<string> result, StringBuilder current, int limit)
	{
		while (current.Length >= limit)
		{
			int splitAt = FindSplitPoint(current, limit);
			string piece = current.ToString(0, splitAt).Trim();
			if (piece.Length > 0) result.Add(piece);
			current.Remove(0, splitAt);
		}
	}

	private static int FindSplitPoint(StringBuilder current, int limit)
	{
		int start = Math.Min(limit, current.Length);
		for (int index = start; index > 0; index--)
		{
			if (SoftTerminators.Contains(current[index - 1]) || char.IsWhiteSpace(current[index - 1])) return index;
		}
		return start;
	}
}

/// <summary>按最近最少使用策略保存合成音频。</summary>
public sealed class AudioSynthesisCache
{
	private sealed record CacheEntry(string Key, EncodedAudio Audio, long Size);

	private readonly object _gate = new();
	private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.Ordinal);
	private readonly LinkedList<CacheEntry> _lru = new();
	private long _bytes;

	/// <summary>当前缓存条目数。</summary>
	public int Count
	{
		get { lock (_gate) return _entries.Count; }
	}

	/// <summary>当前缓存占用字节数。</summary>
	public long Bytes
	{
		get { lock (_gate) return _bytes; }
	}

	/// <summary>按提供商端点、音色、语速、情绪和文本哈希生成稳定键。</summary>
	public static string CreateKey(string providerEndpoint, string? voice, double speed, string text, string? emotion = null)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
		return $"{providerEndpoint.Trim()}\n{voice?.Trim() ?? ""}\n{speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n{emotion?.Trim() ?? ""}\n{Convert.ToHexString(hash)}";
	}

	/// <summary>读取缓存并更新最近使用顺序。</summary>
	public bool TryGet(string key, out EncodedAudio audio)
	{
		lock (_gate)
		{
			if (!_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
			{
				audio = null!;
				return false;
			}
			_lru.Remove(node);
			_lru.AddFirst(node);
			audio = node.Value.Audio;
			return true;
		}
	}

	/// <summary>写入缓存，超出条目或总字节上限时淘汰最旧条目。</summary>
	public void Put(string key, EncodedAudio audio)
	{
		if (audio.Bytes.Length == 0 || audio.Bytes.Length > VoiceAudioLimits.MaxBytes) return;
		lock (_gate)
		{
			if (_entries.Remove(key, out LinkedListNode<CacheEntry>? old))
			{
				_bytes -= old.Value.Size;
				_lru.Remove(old);
			}

			CacheEntry entry = new(key, audio, audio.Bytes.Length);
			LinkedListNode<CacheEntry> node = _lru.AddFirst(entry);
			_entries[key] = node;
			_bytes += entry.Size;
			while (_entries.Count > VoiceAudioLimits.CacheItemLimit || _bytes > VoiceAudioLimits.MaxBytes)
			{
				LinkedListNode<CacheEntry>? last = _lru.Last;
				if (last is null) break;
				_lru.RemoveLast();
				_entries.Remove(last.Value.Key);
				_bytes -= last.Value.Size;
			}
		}
	}

	/// <summary>清空缓存。</summary>
	public void Clear()
	{
		lock (_gate)
		{
			_entries.Clear();
			_lru.Clear();
			_bytes = 0;
		}
	}
}
