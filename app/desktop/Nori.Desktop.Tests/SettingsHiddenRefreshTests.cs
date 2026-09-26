using Nori.Core.Configuration;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task 隐藏的设置窗口把快照刷新推迟到再次显示() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new(fixture._services);
		try
		{
			window.Show();
			SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);
			await WaitUntilAsync(() => viewModel.Language == "zh-CN" && viewModel.ErrorMessage.Length == 0);

			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			fixture._runtime.InvalidateSnapshot();
			await WaitUntilAsync(() => viewModel.Language == "en-US");

			window.Close();
			await WaitUntilAsync(() => !window.IsVisible);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("zh-CN"));
			fixture._runtime.InvalidateSnapshot();
			await Task.Delay(150);
			Assert.Equal("en-US", viewModel.Language);

			await viewModel.RefreshSnapshotAsync();
			Assert.Equal("zh-CN", viewModel.Language);

			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			fixture._runtime.InvalidateSnapshot();
			await Task.Delay(150);
			Assert.Equal("zh-CN", viewModel.Language);
			Assert.True(await viewModel.FlushPendingSavesAsync());

			window.Show();
			await WaitUntilAsync(() => viewModel.Language == "en-US");
			await viewModel.PrepareShutdownAsync();
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});
}
