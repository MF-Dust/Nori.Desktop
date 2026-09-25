using Nori.Desktop.Settings;

namespace Nori.Desktop.Tests;

public sealed class SettingsErrorTextTests
{
	[Fact]
	public void 英文设置页翻译宿主错误()
	{
		SettingsLocalization.SetLanguage("en-US");
		try
		{
			Assert.Equal("The plugin runtime is not ready.", SettingsErrorText.Resolve(new InvalidOperationException("插件运行时尚未就绪")));
			Assert.Equal("The provided value is invalid.", SettingsErrorText.Resolve("输入不能为空"));
		}
		finally
		{
			SettingsLocalization.SetLanguage("zh-CN");
		}
	}

	[Fact]
	public void 中文设置页保留宿主错误()
	{
		SettingsLocalization.SetLanguage("zh-CN");
		Assert.Equal("插件运行时尚未就绪", SettingsErrorText.Resolve(new InvalidOperationException("插件运行时尚未就绪")));
	}
}
