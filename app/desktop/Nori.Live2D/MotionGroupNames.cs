namespace Nori.Live2D;

/// <summary>
/// 按动作组名字判断它能不能当作待机或语音。
/// 只扫描有限长度，避免超长组名在渲染线程上撑爆栈。
/// </summary>
public static class MotionGroupNames
{
	private const int MaxScanLength = 128;

	/// <summary>名称以 idle 开头，且不是点击、触摸类互动组。</summary>
	public static bool IsIdle(string? group)
	{
		Span<char> buffer = stackalloc char[MaxScanLength];
		int length = Normalize(group, buffer);
		return IsIdle(buffer[..length]);
	}

	/// <summary>说话或语音组。待机和点击组不在这里。</summary>
	public static bool IsVoice(string? group)
	{
		Span<char> buffer = stackalloc char[MaxScanLength];
		int length = Normalize(group, buffer);
		ReadOnlySpan<char> normalized = buffer[..length];
		if (normalized.IsEmpty || IsIdle(normalized) || IsInteraction(normalized)) return false;
		return normalized.Contains("talk", StringComparison.Ordinal)
			|| normalized.Contains("speak", StringComparison.Ordinal)
			|| normalized.Contains("voice", StringComparison.Ordinal)
			|| normalized.SequenceEqual("chat");
	}

	private static bool IsIdle(ReadOnlySpan<char> normalized) =>
		normalized.StartsWith("idle", StringComparison.Ordinal) && !IsInteraction(normalized);

	private static bool IsInteraction(ReadOnlySpan<char> normalized) =>
		normalized.Contains("tap", StringComparison.Ordinal)
		|| normalized.Contains("touch", StringComparison.Ordinal)
		|| normalized.Contains("click", StringComparison.Ordinal);

	/// <summary>把组名收成小写字母数字，写进调用方提供的缓冲，不分配新字符串。</summary>
	private static int Normalize(string? value, Span<char> buffer)
	{
		if (string.IsNullOrWhiteSpace(value)) return 0;
		int limit = Math.Min(value.Length, buffer.Length);
		int length = 0;
		for (int index = 0; index < limit; index++)
		{
			char character = value[index];
			if (!char.IsLetterOrDigit(character)) continue;
			buffer[length++] = char.ToLowerInvariant(character);
		}
		return length;
	}
}
