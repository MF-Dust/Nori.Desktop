using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 原生首次运行向导。
///
/// 步进规则本身在 <c>Nori.Core.Tests.FirstRunWizardTests</c> 里测（那层不碰 UI）。
/// 这一族测的是**接线**：窗口是否正确呈现状态机、各步的阻断是否落到底部错误行、
/// 完成后是否写入配置。
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
						// 测试环境无已安装模型，选形象步骤会触发阻断；截图需要遍历全部
						// 五个步骤，此处跳过阻断继续。
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
	/// 选形象步骤：测试环境无已安装模型，须阻断并将原因写入底部错误行。
	///
	/// 守的是原有缺陷：失败仅输出到控制台时，界面无任何变化。
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

				// 阻断须实际阻止步进。
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

	/// <summary>
	/// 触发阻断的步骤必须同时提供解除阻断的操作。
	///
	/// 守的是一处实际存在过的缺陷：形象资源**不随安装包发行**（仅支持本地 ZIP/目录
	/// 导入），全新安装时已安装列表为空；本步骤阻断 CanNext，末步 CompleteFirstRun
	/// 又要求非空 modelId。Vue 版 ModelSelect 提供导入入口，原生版只迁移了选择，
	/// 未迁移导入，阻断条件保留 —— 初始化流程因此无法完成，且无任何错误输出。
	///
	/// 断言的不是「存在两个按钮」，而是**该步骤在触发自身阻断时仍可操作**。
	/// </summary>
	[Fact]
	public async Task 选形象那一步挡住时仍给得出导入的路()
	{
		await WithSettingsUiAsync(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: false);
			FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
			try
			{
				window.Show();
				await window.AdvanceForTests();          // → language
				await window.AdvanceForTests();          // → model
				Assert.Equal(WizardStep.Model, window.CurrentStepForTests);
				// 前置条件：该步骤确实触发了阻断，否则本测试不成立。
				Assert.False(window.ForwardForTests.Enabled);

				// 执行一次布局，否则内容区控件尚未进入可视树，无法枚举到按钮。
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

				string[] actions = [.. window.GetVisualDescendants().OfType<Button>()
					.Select(button => new {button.IsEnabled, Label = button.Content as string ?? ""})
					.Where(entry => entry.IsEnabled && entry.Label.Contains("导入"))
					.Select(entry => entry.Label)];

				Assert.True(actions.Length > 0,
					"选形象步骤阻断了「下一步」，但无可用的导入入口 —— 全新安装时初始化流程无法完成。");
			}
			finally
			{
				window.AllowClose = true;
				window.Close();
			}
		});
	}

	/// <summary>后退须清除错误行：返回修改时不应保留上一步的错误提示。</summary>
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

	/// <summary>末步按钮须切换为「开始使用」，以区别于中间步骤的「下一步」。</summary>
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
	/// 未选形象时点击「开始使用」：须给出面向用户的错误说明，且不得写入首次运行标记。
	///
	/// 正常路径由选形象步骤阻断，本条守的是兜底校验 —— 缺少它时界面会显示配置层
	/// 抛出的「模型 ID 不能为空」。
	/// </summary>
	[Fact]
	public async Task 未选形象时完成失败且不写入首次运行标记()
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
				Assert.Equal("未选择形象，无法完成初始化", window.ErrorTextForTests);
				// 失败后须可在当前步骤重试。
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
