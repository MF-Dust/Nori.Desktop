namespace Nori.Core.Voice.Audio;

/// <summary>
/// 用哪一套音频后端。
///
/// 判定单独拎出来是因为它要能测：分流写在 <c>AppRuntime</c> 的构造函数里，
/// 而那个构造函数要拉起半个应用才跑得起来。
/// </summary>
public static class AudioBackend
{
	/// <summary>按平台自动挑。</summary>
	public const string Auto = "auto";

	/// <summary>强制直接推声卡。</summary>
	public const string Native = "native";

	/// <summary>强制走 WebView 的 WebAudio / MediaRecorder。</summary>
	public const string WebView = "webview";

	/// <summary>
	/// 这一轮该不该用原生后端。
	///
	/// <paramref name="nativeAvailable"/> 是「这个平台有没有原生实现」——
	/// 眼下只有 Windows 有（WASAPI），CoreAudio 与 ALSA 还没做。
	///
	/// 显式选 <see cref="Native"/> 时**仍然要看平台**：在没有实现的平台上硬走原生
	/// 等于没有声音，而用户改这个开关的本意是修声音，不是关掉它。
	/// 认不出来的值按 <see cref="Auto"/> 处理 —— 配置被写坏不该让应用哑掉。
	/// </summary>
	public static bool PrefersNative(string? configured, bool nativeAvailable) =>
		configured?.Trim().ToLowerInvariant() switch
		{
			WebView => false,
			_ => nativeAvailable,
		};
}
