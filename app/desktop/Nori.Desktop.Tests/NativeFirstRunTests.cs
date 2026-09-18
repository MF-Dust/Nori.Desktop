using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 原生首次运行向导。
///
/// 步进规则本身在 <c>Nori.Core.Tests.FirstRunWizardTests</c> 里测（那层不碰 UI）。
/// 这一族测的是**接线**：窗口有没有把状态机画对、每一步的阻断有没有落到底部那条
/// 错误行上、完成之后有没有真的写进配置。
/// </summary>
public partial class BridgeCommandsTests
{
	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeFirstRunVisualFactAttribute : FactAttribute
	{
		public NativeFirstRunVisualFactAttribute()
		{
			if (Environment.GetEnvironmentVariable("NORI_CAPTURE_FIRSTRUN") != "1")
				Skip = "由视觉步骤使用 NORI_CAPTURE_FIRSTRUN=1 执行真实渲染。";
		}
	}

	private static WindowDefinition FirstRunDefinition() =>
		WindowDefinition.All.Single(item => item.Label == WindowLabels.FirstRun);

	[NativeFirstRunVisualFact]
	public async Task 原生首次运行向导能截出每一步()
	{
		string outputDirectory = Path.Combine(NativeSettingsCaptureDirectory(), "..", "native-first-run");
		Directory.CreateDirectory(outputDirectory);

		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			foreach (string language in new[] {"zh-CN", "en-US"})
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
				try
				{
					window.Show();
					foreach (WizardStep step in FirstRunWizard.Order)
					{
						await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
						AvaloniaHeadlessPlatform.ForceRenderTimerTick();
						await Task.Delay(80);
						AvaloniaHeadlessPlatform.ForceRenderTimerTick();

						using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
						frame.Save(
							Path.Combine(outputDirectory, $"firstrun-{language}-{step}.png".ToLowerInvariant()),
							PngBitmapEncoderOptions.Default);

						if (step == WizardStep.Ready) break;
						// 选形象那一步在测试环境里没有已安装的模型，会被自己挡住；
						// 截图流程只要走完五页，这里放开阻断继续。
						await window.AdvanceForTests();
						if (window.CurrentStepForTests == step) window.ForceStepForTests();
					}
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
	public async Task 打开时停在欢迎页且没有上一步()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				Assert.Equal(WizardStep.Welcome, window.CurrentStepForTests);
				Assert.Equal("下一步", window.ForwardForTests.Text);
				Assert.True(window.ForwardForTests.Enabled);
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
	/// 选形象那一步：测试环境里一个模型都没装，必须挡住并把原因写到底部。
	///
	/// 这条守的是最初那个问题 —— 失败只往控制台打一行时，用户看到的就是「卡住了」。
	/// </summary>
	[Fact]
	public async Task 没有已安装的形象时挡住并给出原因()
	{
		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				await window.AdvanceForTests();          // → language
				await window.AdvanceForTests();          // → model
				Assert.Equal(WizardStep.Model, window.CurrentStepForTests);

				Assert.Contains("形象", window.ErrorTextForTests);
				Assert.False(window.ForwardForTests.Enabled);

				// 挡住就是真的过不去。
				await window.AdvanceForTests();
				Assert.Equal(WizardStep.Model, window.CurrentStepForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
		});
	}

	/// <summary>后退要把错误行清掉 —— 回去改东西时不该还挂着上一步的红字。</summary>
	[Fact]
	public async Task 后退清掉底部的错误行()
	{
		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				await window.AdvanceForTests();
				await window.AdvanceForTests();
				Assert.NotEmpty(window.ErrorTextForTests);

				window.BackForTests();
				Assert.Equal(WizardStep.Language, window.CurrentStepForTests);
				Assert.Empty(window.ErrorTextForTests);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
		});
	}

	/// <summary>文案跟界面语言走，不跟系统区域。</summary>
	[Theory]
	[InlineData("zh-CN", "下一步")]
	[InlineData("en-US", "Next")]
	public async Task 导航按钮按界面语言出文案(string language, string expected)
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				Assert.Equal(expected, window.ForwardForTests.Text);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
			return Task.CompletedTask;
		});
	}

	/// <summary>末步的按钮要换成「开始使用」，否则用户不知道这是最后一下。</summary>
	[Fact]
	public async Task 末步的按钮换成开始使用()
	{
		await WithSettingsUiAsync(() =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				window.ForceStepForTests(WizardStep.Ready);
				Assert.Equal("开始使用", window.ForwardForTests.Text);
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
	/// 没选形象就按「开始使用」：要给一句**人话**，并且首次运行标记不能被写掉。
	///
	/// 正常路径上选形象那一步就挡住了，这条守的是兜底那一层 —— 少了它，用户会看到
	/// 配置层抛出来的「模型 ID 不能为空」。
	/// </summary>
	[Fact]
	public async Task 没选形象时完成给的是人话且不写标记()
	{
		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				window.ForceStepForTests(WizardStep.Ready);
				await window.AdvanceForTests();

				Assert.True(fixture._config.IsFirstRun());
				Assert.Equal("开始之前要先选一个形象", window.ErrorTextForTests);
				// 失败要能原地重试。
				Assert.Equal("重试", window.ForwardForTests.Text);
				Assert.True(window.ForwardForTests.Enabled);
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
		});
	}

	/// <summary>完成之后首次运行标记要真的写进配置，否则下次启动又是向导。</summary>
	[Fact]
	public async Task 完成之后写入首次运行标记()
	{
		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			Assert.True(fixture._config.IsFirstRun());

			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				window.SelectModelForTests("nori");
				window.ForceStepForTests(WizardStep.Ready);
				await window.AdvanceForTests();

				Assert.False(fixture._config.IsFirstRun());
				Assert.Empty(window.ErrorTextForTests);
				Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
		});
	}
}
