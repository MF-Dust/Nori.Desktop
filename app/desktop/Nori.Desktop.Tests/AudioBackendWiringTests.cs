using Nori.Core.Configuration;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Runtime;

namespace Nori.Desktop.Tests;

/// <summary>
/// 运行时装配的是哪一套音频后端。
///
/// 判定本身在 <c>Nori.Core.Tests.AudioBackendTests</c> 里测（那层不碰运行时）。
/// 这一族测的是**接线**：配置真的被读到了、分流真的走到了对应的分支。
///
/// 值得单独钉住的理由：这类改动一旦悄悄回退到旧路径，现象只是「声音还是老样子」，
/// 没有报错也没有异常，很难发现。
/// </summary>
public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 默认按平台挑音频后端()
	{
		AppRuntime runtime = new(_services);

		Assert.Equal(OperatingSystem.IsWindows() ? "native" : "webview", runtime.AudioBackendName);
		await runtime.DisposeAsync();
	}

	/// <summary>
	/// 配置里写 webview 就退回老路径。
	///
	/// 这一档存在的意义是：原生后端在某台机器上出问题时，用户改一个配置就能拿回声音，
	/// 不必等修复版本。
	/// </summary>
	[Fact]
	public async Task 配置写webview时退回老路径()
	{
		_config.Set(ConfigStore.KeyAudioBackend, new ConfigValue.Text(AudioBackend.WebView));

		AppRuntime runtime = new(_services);

		Assert.Equal("webview", runtime.AudioBackendName);
		await runtime.DisposeAsync();
	}

	/// <summary>写坏的值不该让应用哑掉 —— 按平台默认走。</summary>
	[Theory]
	[InlineData("")]
	[InlineData("WEBAUDIO")]
	public async Task 配置写坏时按平台挑(string configured)
	{
		_config.Set(ConfigStore.KeyAudioBackend, new ConfigValue.Text(configured));

		AppRuntime runtime = new(_services);

		Assert.Equal(OperatingSystem.IsWindows() ? "native" : "webview", runtime.AudioBackendName);
		await runtime.DisposeAsync();
	}
}
