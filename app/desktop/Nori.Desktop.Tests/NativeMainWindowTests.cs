using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

					// 首页高于窗口，只截首屏时下半页（快速前往的第二行、生态社区）从不进入
					// 复核范围。滚到底再截一帧。
					foreach (ScrollViewer scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
						scroller.ScrollToEnd();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(120);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap bottom = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					bottom.Save(
						Path.Combine(outputDirectory, $"main-{language}-bottom.png"),
						PngBitmapEncoderOptions.Default);

					// 收起态只能在这里复核：它不是默认状态，别的截图都看不到。
					window.SetCollapsedForTests(true);
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(120);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap narrow = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					narrow.Save(
						Path.Combine(outputDirectory, $"main-{language}-collapsed.png"),
						PngBitmapEncoderOptions.Default);
					window.SetCollapsedForTests(false);
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
	/// 收起侧边栏：只留图标列，且选择写进配置 —— 下次开窗要保持上次的选择。
	///
	/// 守的是「收起」这个状态只活在内存里的写法：那样每次开窗都回到展开，用户每次都要
	/// 重新收一遍。
	/// </summary>
	[Fact]
	public async Task 侧边栏收起后只留图标且下次开窗保持()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			double wide;
			try
			{
				wide = window.SidebarWidthForTests;
				Assert.All(window.LauncherLabelsVisibleForTests, Assert.True);

				window.SetCollapsedForTests(true);
				Assert.True(window.SidebarWidthForTests < wide);
				Assert.All(window.LauncherLabelsVisibleForTests, Assert.False);
				// 四个启动器仍在，只是不显示文字。
				Assert.Equal(4, window.LauncherLabelsForTests.Count);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}

			MainWindow reopened = new(MainDefinition(), fixture._services);
			try
			{
				Assert.True(reopened.SidebarWidthForTests < wide);
			}
			finally
			{
				reopened.AllowClose = true;
				reopened.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>
	/// 状态没变就不要重建控件。
	///
	/// 这个窗口由一个 2 秒的定时器驱动重画。无条件重建的代价不只是分配：侧边栏那五行
	/// 和首页的卡片每 2 秒换成新控件，指针停在上面时悬停底色被重置，首页上「已复制群号」
	/// 这类操作反馈最多活 2 秒、通常更短。
	///
	/// 断言的是「同一个控件实例还在」，不是某个外观属性 —— 重建之后外观一样，但实例换了。
	/// </summary>
	[Fact]
	public async Task 状态没变时不重建侧边栏与首页()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				object launcher = window.LauncherControlsForTests[0];
				object home = window.HomeChildrenForTests[0];

				window.RefreshForTests();
				Assert.Same(launcher, window.LauncherControlsForTests[0]);
				Assert.Same(home, window.HomeChildrenForTests[0]);

				// 状态变了就必须重建，否则界面会停在旧值上。
				fixture._windows.SetVisible(WindowLabels.Memory, true);
				window.RefreshForTests();
				Assert.NotSame(launcher, window.LauncherControlsForTests[0]);
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
	/// 侧边栏收起状态写的是网页端那一个键。
	///
	/// 另起一个键不会报错，后果是两边各记各的，而且只有 <c>ui_sidebar_collapsed</c>
	/// 在云存档白名单里 —— 新键换一台机器就丢。
	/// </summary>
	[Fact]
	public async Task 侧边栏收起写进网页端同一个配置键()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				window.SetCollapsedForTests(true);
				Assert.True(fixture._config.GetBoolOr("ui_sidebar_collapsed", false));

				window.SetCollapsedForTests(false);
				Assert.False(fixture._config.GetBoolOr("ui_sidebar_collapsed", true));
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
	/// 待办标记只挂在设置项上，且只在模型服务未配置时出现。
	///
	/// 它与「窗口开着」那颗青色点是两件事，共用一个启动器行 —— 两者都按名字取用，
	/// 这条同时钉住取用方式：按 Children 下标取时，在行内插入一个图标就会取到别的控件。
	/// </summary>
	[Fact]
	public async Task 模型服务未配置时设置项亮待办标记()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			MainWindow window = new(MainDefinition(), fixture._services);
			try
			{
				// 顺序是 对话 / 模型 / 记忆 / 设置。
				Assert.Equal([false, false, false, true], window.LauncherBadgesForTests);

				fixture._services.AiSettings.UpdateChat(new AiChatSettingsPatch(
					BaseUrl: "https://example.invalid/v1",
					ApiKey: "k",
					Model: "m",
					ApiKeySpecified: true));
				window.RefreshForTests();

				Assert.All(window.LauncherBadgesForTests, Assert.False);
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
