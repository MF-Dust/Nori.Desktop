using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 原生初始化窗口。
///
/// 分两档：功能那几条普通单测就跑（不渲染，只看状态机 —— 什么时候起跑、超时面板
/// 什么时候出来、语言切了文案跟不跟）；截图那条和设置页一样只在视觉 CI 里跑，
/// 因为它要真实 Skia。
/// </summary>
public partial class BridgeCommandsTests
{
	/// <summary>只在专门的视觉步骤里启用真实渲染。</summary>
	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeInitVisualFactAttribute : FactAttribute
	{
		public NativeInitVisualFactAttribute()
		{
			if (Environment.GetEnvironmentVariable("NORI_CAPTURE_INIT") != "1")
				Skip = "由视觉步骤使用 NORI_CAPTURE_INIT=1 执行真实渲染。";
		}
	}

	private static WindowDefinition InitDefinition() =>
		WindowDefinition.All.Single(item => item.Label == WindowLabels.Init);

	[NativeInitVisualFact]
	public async Task 原生初始化窗口能截出一帧()
	{
		string outputDirectory = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-init");
		Directory.CreateDirectory(outputDirectory);

		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			foreach (string language in new[] {"zh-CN", "en-US"})
			foreach (bool timedOut in new[] {false, true})
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				InitWindow window = new(InitDefinition(), fixture._services);
				try
				{
					window.Show();
					if (timedOut) window.ShowTimeoutForTests();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(120);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();

					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					frame.Save(
						Path.Combine(outputDirectory, $"init-{language}-{(timedOut ? "timeout" : "loading")}.png"),
						PngBitmapEncoderOptions.Default);
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

	// ── 状态机 ─────────────────────────────────────────────────────────────

	/// <summary>
	/// 首次运行路径下这个窗口是隐藏启动的。原来靠 <c>nori:init-start</c> 广播叫醒，
	/// 现在是原生窗口，「变可见」本身就是那个信号 —— 这条钉住它别再需要一条事件。
	/// </summary>
	[Fact]
	public async Task 隐藏启动时不自己进主界面()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			InitWindow window = new(InitDefinition(), fixture._services);
			try
			{
				// 没 Show 过：既不可见，宿主也没置位，就不该把主界面拉起来。
				Assert.False(window.HasStartedForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>文案跟着界面语言走，不跟系统区域。</summary>
	[Theory]
	[InlineData("zh-CN", "进入主界面")]
	[InlineData("en-US", "Open main window")]
	public async Task 超时面板按界面语言出文案(string language, string expected)
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
			InitWindow window = new(InitDefinition(), fixture._services);
			try
			{
				window.ShowTimeoutForTests();
				Assert.Equal(expected, window.RetryLabelForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>窗口尺寸与不可缩放这两项来自同一份窗口定义，不在原生实现里另写一份。</summary>
	[Fact]
	public async Task 尺寸沿用窗口定义()
	{
		WindowDefinition definition = InitDefinition();
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			InitWindow window = new(definition, fixture._services);
			try
			{
				Assert.Equal(definition.Width, window.Width);
				Assert.Equal(definition.Height, window.Height);
				Assert.Equal(definition.CanResize, window.CanResize);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>普通关闭只隐藏 —— 初始化还没走完就把窗口关掉等于卡在没有界面的状态。</summary>
	[Fact]
	public async Task 普通关闭只隐藏()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			InitWindow window = new(InitDefinition(), fixture._services);
			try
			{
				window.Show();
				window.Close();
				Assert.False(window.IsVisible);
				// 没被真的关掉：还能再显示出来。
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
}
