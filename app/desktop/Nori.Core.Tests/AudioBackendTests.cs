using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>
/// 选哪一套音频后端。
///
/// 这段判定写在 AppRuntime 的构造函数里，而那个构造函数要拉起半个应用才跑得起来 ——
/// 所以拎出来单测。它决定用户听不听得见声音，值得钉死。
/// </summary>
public sealed class AudioBackendTests
{
	[Fact]
	public void 默认按平台挑()
	{
		Assert.True(AudioBackend.PrefersNative(AudioBackend.Auto, nativeAvailable: true));
		Assert.False(AudioBackend.PrefersNative(AudioBackend.Auto, nativeAvailable: false));
	}

	[Fact]
	public void 显式选WebView就不用原生()
	{
		Assert.False(AudioBackend.PrefersNative(AudioBackend.WebView, nativeAvailable: true));
	}

	/// <summary>
	/// 显式选 native 时**仍然要看平台**。
	///
	/// 在没有实现的平台上硬走原生等于没有声音，而用户改这个开关的本意是修声音，
	/// 不是关掉它。
	/// </summary>
	[Fact]
	public void 显式选原生但平台没有实现时仍然退回()
	{
		Assert.False(AudioBackend.PrefersNative(AudioBackend.Native, nativeAvailable: false));
		Assert.True(AudioBackend.PrefersNative(AudioBackend.Native, nativeAvailable: true));
	}

	/// <summary>配置被写坏不该让应用哑掉 —— 认不出来的值按 auto 走。</summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("WEBAUDIO")]
	[InlineData("原生")]
	public void 认不出来的值按平台挑(string? configured)
	{
		Assert.True(AudioBackend.PrefersNative(configured, nativeAvailable: true));
		Assert.False(AudioBackend.PrefersNative(configured, nativeAvailable: false));
	}

	/// <summary>大小写和空白不该让开关失效 —— 用户是手改配置的。</summary>
	[Theory]
	[InlineData("WebView")]
	[InlineData("  webview  ")]
	[InlineData("WEBVIEW")]
	public void 开关不区分大小写与空白(string configured)
	{
		Assert.False(AudioBackend.PrefersNative(configured, nativeAvailable: true));
	}
}
