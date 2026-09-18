using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 原生主界面。
///
/// 这一族盯的是上一轮专门改过、**很容易在重写时丢掉**的那几条：
/// 「主页」与四个启动器形状不同、圆点只表示「开着」、平台能力缺失时要说出来。
/// </summary>
public partial class BridgeCommandsTests
{
	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeMainVisualFactAttribute : FactAttribute
	{
		public NativeMainVisualFactAttribute()
		{
			if (Environment.GetEnvironmentVariable("NORI_CAPTURE_MAIN") != "1")
				Skip = "由视觉步骤使用 NORI_CAPTURE_MAIN=1 执行真实渲染。";
		}
	}

	private static WindowDefinition MainDefinition() =>
		WindowDefinition.All.Single(item => item.Label == WindowLabels.Main);

	[NativeMainVisualFact]
	public async Task 原生主界面能截出一帧()
	{
		string outputDirectory = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-main");
		Directory.CreateDirectory(outputDirectory);

		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			foreach (string language in new[] {"zh-CN", "en-US"})
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				MainWindow window = new(MainDefinition(), fixture._services);
				try
				{
					window.Show();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(120);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					frame.Save(Path.Combine(outputDirectory, $"main-{language}.png"), PngBitmapEncoderOptions.Default);
				}
				finally
				{
					window.AllowClose = true;
					window.Close();
				}
			}
			return true;
		}, CancellationToken.None);
	}

	// ── 接线 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 侧边栏四个启动器按界面语言出文案()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				Assert.Equal(["对话", "模型", "记忆", "设置"], window.LauncherLabelsForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	[Fact]
	public async Task 英文界面下启动器也跟着换()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				Assert.Equal(["Chat", "Models", "Memory", "Settings"], window.LauncherLabelsForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>
	/// 圆点只在窗口**开着**时亮，而且只表示这一件事。
	///
	/// 上一轮试过给关闭态也画一个暗点，在 6 像素的圆上那个颜色根本看不出来 ——
	/// 等于一个看不见的提示。这条钉住「关着就是不画」。
	/// </summary>
	[Fact]
	public async Task 圆点只表示那个窗口开着()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				Assert.All(window.LauncherDotsForTests, Assert.False);

				fixture._windows.SetVisible(WindowLabels.Memory, true);
				window.RefreshForTests();

				// 顺序是 对话 / 模型 / 记忆 / 设置，只有第三个该亮。
				Assert.Equal([false, false, true, false], window.LauncherDotsForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>普通关闭只收起 —— 托盘还在，伴侣可能还在桌面上。</summary>
	[Fact]
	public async Task 关闭主界面只是收起()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				window.Show();
				window.Close();
				Assert.False(window.IsVisible);
				window.Show();
				Assert.True(window.IsVisible);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>
	/// 托盘不可用时要在窗口里说出来。
	///
	/// 那种桌面环境下这个窗口是找到 Nori 的唯一入口，不说的话用户关掉就再也找不回来。
	/// </summary>
	[Fact]
	public async Task 托盘不可用时给出提示()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				fixture._runtime.TrayAvailable = true;
				window.RefreshForTests();
				Assert.Empty(window.HintsForTests);

				fixture._runtime.TrayAvailable = false;
				window.RefreshForTests();
				Assert.Contains("托盘", window.HintsForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}
}
