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
		string normalized = Normalize(group);
		return normalized.StartsWith("idle", StringComparison.Ordinal) && !IsInteraction(normalized);
	}

	/// <summary>说话或语音组。待机和点击组不在这里。</summary>
	public static bool IsVoice(string? group)
	{
		string normalized = Normalize(group);
		if (normalized.Length == 0 || IsIdle(group) || IsInteraction(normalized)) return false;
		return normalized.Contains("talk", StringComparison.Ordinal)
			|| normalized.Contains("speak", StringComparison.Ordinal)
			|| normalized.Contains("voice", StringComparison.Ordinal)
			|| normalized == "chat";
	}

	private static bool IsInteraction(string normalized) =>
		normalized.Contains("tap", StringComparison.Ordinal)
		|| normalized.Contains("touch", StringComparison.Ordinal)
		|| normalized.Contains("click", StringComparison.Ordinal);

	private static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "";
		int limit = Math.Min(value.Length, MaxScanLength);
		Span<char> buffer = stackalloc char[MaxScanLength];
		int length = 0;
		for (int index = 0; index < limit; index++)
		{
			char character = value[index];
			if (!char.IsLetterOrDigit(character)) continue;
			buffer[length++] = char.ToLowerInvariant(character);
		}
		return new string(buffer[..length]);
	}
}
