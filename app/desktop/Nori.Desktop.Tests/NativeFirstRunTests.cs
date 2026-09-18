using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Resources;
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

	[Theory]
	[InlineData("zip")]
	[InlineData("folder")]
	public Task 本地导入刷新列表选中新模型且期间禁止前进和重复导入(string sourceKind) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.InstallKnownModel("nori");
		fixture._config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("nori"));
		string source = CreateFirstRunImportSource(fixture._tempDir, "arg-nori", sourceKind);
		TaskCompletionSource<string?> picked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int picks = 0;
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services, kind =>
		{
			Assert.Equal(sourceKind, kind);
			picks++;
			return picked.Task;
		});
		Task? importing = null;
		try
		{
			window.ForceStepForTests(WizardStep.Model);
			Assert.Equal("nori", window.SelectedModelForTests);
			importing = window.ImportModelForTests(sourceKind);
			Assert.False(window.ForwardForTests.Enabled);
			Assert.All(FirstRunImportButtons(window), button => Assert.False(button.IsEnabled));
			await window.AdvanceForTests();
			await window.ImportModelForTests(sourceKind);
			Assert.Equal(WizardStep.Model, window.CurrentStepForTests);
			Assert.Equal(1, picks);

			picked.SetResult(source);
			await importing;
			Assert.True(fixture._services.Resources.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.Equal("arg-nori", window.SelectedModelForTests);
			Assert.Equal(2, window.GetLogicalDescendants().OfType<TextBlock>().Count(text => text.Text == "已安装"));
			Assert.Empty(window.ErrorTextForTests);
			Assert.True(window.ForwardForTests.Enabled);
			Assert.All(FirstRunImportButtons(window), button => Assert.True(button.IsEnabled));
			// 导入不提前提交首次运行配置；末步才写入当前选择。
			Assert.True(fixture._config.IsFirstRun());
			Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			await window.AdvanceForTests();
			await window.AdvanceForTests();
			await window.AdvanceForTests();
			Assert.False(fixture._config.IsFirstRun());
			Assert.Equal("arg-nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		}
		finally
		{
			picked.TrySetResult(null);
			if (importing is not null) await importing;
			window.AllowClose = true;
			window.Close();
		}
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task 取消导入保留选择且零模型仍不能前进(bool installed) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		if (installed) fixture.InstallKnownModel("nori");
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services, _ => Task.FromResult<string?>(null));
		try
		{
			window.ForceStepForTests(WizardStep.Model);
			string selected = window.SelectedModelForTests;
			string error = window.ErrorTextForTests;
			await window.ImportModelForTests("zip");
			Assert.Equal(selected, window.SelectedModelForTests);
			Assert.Equal(error, window.ErrorTextForTests);
			Assert.Equal(installed, window.ForwardForTests.Enabled);
			Assert.All(FirstRunImportButtons(window), button => Assert.True(button.IsEnabled));
			await window.AdvanceForTests();
			Assert.Equal(installed ? WizardStep.Ai : WizardStep.Model, window.CurrentStepForTests);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 导入校验失败保留选择并可重试() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.InstallKnownModel("nori");
		string source = CreateFirstRunImportSource(fixture._tempDir, "arg-nori", "folder");
		File.Delete(Path.Combine(source, "model.moc3"));
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services, _ => Task.FromResult<string?>(source));
		try
		{
			window.ForceStepForTests(WizardStep.Model);
			await window.ImportModelForTests("folder");
			Assert.Contains("导入失败，请重试", window.ErrorTextForTests);
			Assert.Equal("nori", window.SelectedModelForTests);
			Assert.False(fixture._services.Resources.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.True(fixture._services.Resources.IsInstalled(ResourceType.Live2D, "nori"));
			Assert.All(FirstRunImportButtons(window), button => Assert.True(button.IsEnabled));

			File.WriteAllText(Path.Combine(source, "model.moc3"), "MOC3");
			await window.ImportModelForTests("folder");
			Assert.Equal("arg-nori", window.SelectedModelForTests);
			Assert.Empty(window.ErrorTextForTests);
			Assert.True(window.ForwardForTests.Enabled);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 失效的模型选择不能绕过前进或完成守卫() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services);
		try
		{
			window.SelectModelForTests("nori");
			window.ForceStepForTests(WizardStep.Model);
			Assert.Empty(window.SelectedModelForTests);
			Assert.False(window.ForwardForTests.Enabled);
			fixture.InstallKnownModel("nori");
			window.BackForTests();
			await window.AdvanceForTests();
			Assert.True(window.ForwardForTests.Enabled);
			fixture._services.Resources.Delete(ResourceType.Live2D, "nori");
			await window.AdvanceForTests();
			Assert.Equal(WizardStep.Model, window.CurrentStepForTests);
			Assert.False(window.ForwardForTests.Enabled);
			window.SelectModelForTests("nori");
			window.ForceStepForTests(WizardStep.Ready);
			await window.AdvanceForTests();
			Assert.True(fixture._config.IsFirstRun());
			Assert.Contains("选一个形象", window.ErrorTextForTests);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 关闭向导后取消在途导入不写资源也不重建界面() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource shutdown = new();
		fixture._services.ShutdownToken = shutdown.Token;
		TaskCompletionSource<string?> picked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FirstRunWindow window = new(FirstRunDefinition(), fixture._services, _ => picked.Task);
		Task? importing = null;
		try
		{
			window.ForceStepForTests(WizardStep.Model);
			importing = window.ImportModelForTests("folder");
			Button beforeClose = FirstRunImportButtons(window)[0];
			window.AllowClose = true;
			window.Close();
			shutdown.Cancel();
			picked.SetResult(CreateFirstRunImportSource(fixture._tempDir, "nori", "folder"));
			await importing;
			Assert.False(fixture._services.Resources.IsInstalled(ResourceType.Live2D, "nori"));
			Assert.Same(beforeClose, FirstRunImportButtons(window)[0]);
			Assert.True(fixture._config.IsFirstRun());
		}
		finally
		{
			picked.TrySetResult(null);
			if (importing is not null) await importing;
			window.AllowClose = true;
			window.Close();
		}
	});

	private static Button[] FirstRunImportButtons(FirstRunWindow window)
	{
		Button[] buttons = window.GetLogicalDescendants().OfType<Button>()
			.Where(button => button.Name is "FirstRunImportZip" or "FirstRunImportFolder").ToArray();
		Assert.Equal(2, buttons.Length);
		return buttons;
	}

	private static string CreateFirstRunImportSource(string root, string modelId, string sourceKind)
	{
		string directory = Path.Combine(root, "import-source", modelId);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, modelId + ".model3.json"),
			"{\"FileReferences\":{\"Moc\":\"model.moc3\",\"Textures\":[]}}");
		File.WriteAllText(Path.Combine(directory, "model.moc3"), "MOC3");
		if (sourceKind == "folder") return directory;
		string zip = Path.Combine(root, "appearance.zip");
		ZipFile.CreateFromDirectory(directory, zip);
		return zip;
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
			fixture.InstallKnownModel("nori");

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
