namespace Nori.Desktop.Settings;

/// <summary>原生设置窗口的语言状态。</summary>
public static class SettingsLocalization
{
	private static int _english;

	/// <summary>语言资源发生变化时通知控件重建。</summary>
	public static event Action? Changed;

	/// <summary>当前是否使用英文资源。</summary>
	public static bool IsEnglish => Volatile.Read(ref _english) != 0;

	/// <summary>设置当前界面语言。</summary>
	public static void SetLanguage(string? language)
	{
		int next = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
		if (Interlocked.Exchange(ref _english, next) == next) return;
		Changed?.Invoke();
	}
}
