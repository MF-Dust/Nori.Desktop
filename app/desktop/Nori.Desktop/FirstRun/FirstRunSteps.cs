using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Appearance;
using Nori.Desktop.Models;

namespace Nori.Desktop.FirstRun;

/// <summary>
/// 向导六步各自的样子和各自要调的服务。
///
/// 壳（<c>FirstRunWindow</c>）只管步进和导航，不知道每一步在做什么；步进规则在
/// <see cref="FirstRunWizard"/>。三层分开是为了让规则能脱离 UI 单测。
///
/// 每一步通过 <c>onStepChanged</c> 把「我这一步现在能不能过」抬给壳：给一句话就是
/// 挡住并显示它，给空串就是解除。选形象那一步靠它挡住「一个形象都没有就往下走」。
///
/// 模型服务一步在 <c>FirstRunSteps.Ai.cs</c>，使用方法一步在 <c>FirstRunSteps.Guide.cs</c>。
/// </summary>
public sealed partial class FirstRunSteps(AppServices services, Action<string> onGate, Action onRebuild,
	Func<string, Task<string?>> pickModel, CancellationToken lifetimeToken) : IDisposable
{
	/// <summary>舞台滚动容器的名字。布局测试据此检查每一步在默认窗口里放得下。</summary>
	internal const string StageName = "FirstRunStage";

	private readonly AppServices _services = services;

	/// <summary>报一句阻断原因（空串=解除）。**只刷底部**，不重建舞台。</summary>
	private readonly Action<string> _onGate = onGate;

	/// <summary>要求重建舞台。选形象、换语言这类会改变这一页长相的交互才用它。</summary>
	private readonly Action _onRebuild = onRebuild;

	/// <summary>
	/// 这一步在**刚构建完**时是否被自己挡住（例如一个形象都没装）。
	///
	/// 不在构建过程中回调报出去 —— 那会变成 Build → Render → Build 的递归，
	/// 第一版就是这么栈溢出的。由 Render 在构建之后读一次。
	/// </summary>
	public string Gate { get; private set; } = "";

	private AiDraft _draft = new();
	private string _importError = "";
	private string _downloading = "";
	private string _downloadProgress = "";
	private bool _autoDownloadTried;
	private bool _telemetryInitialized;

	/// <summary>
	/// 形象立绘。下载进度每跳一次都会重建舞台，按 ID 缓存，避免反复解码；
	/// 窗口关闭时由 <see cref="Dispose"/> 释放。
	/// </summary>
	private readonly Dictionary<string, Bitmap?> _thumbnails = [];
	private bool _disposed;

	/// <summary>选择文件或导入期间禁止重复操作和离开当前步骤。</summary>
	public bool IsImporting { get; private set; }

	/// <summary>选中的形象 id。空串表示一个都没装。</summary>
	public string SelectedModel { get; private set; } = "";

	/// <summary>用户在末步选的遥测开关。</summary>
	public bool TelemetryEnabled { get; private set; } = true;

	/// <summary>AI 那一步实际存进去了没有。末步的摘要据此说话。</summary>
	public bool AiSaved { get; private set; }

	/// <summary>步骤标题，画在顶部指示条旁边，也用作欢迎页的路线图。</summary>
	public static string Title(WizardStep step, bool english) => step switch
	{
		WizardStep.Welcome => english ? "Welcome" : "欢迎",
		WizardStep.Language => english ? "Language" : "语言",
		WizardStep.Model => english ? "Appearance" : "形象",
		WizardStep.Ai => english ? "Model provider" : "模型服务",
		WizardStep.Guide => english ? "How to use" : "使用方法",
		_ => english ? "Ready" : "就绪",
	};

	/// <summary>画出某一步。每次进入都重建 —— 这几页都很轻，留着旧实例反而要同步状态。</summary>
	public Control Build(WizardStep step, bool english)
	{
		Gate = "";
		return BuildStep(step, english);
	}

	private Control BuildStep(WizardStep step, bool english) => step switch
	{
		WizardStep.Welcome => BuildWelcome(english),
		WizardStep.Language => BuildLanguage(english),
		WizardStep.Model => BuildModel(english),
		WizardStep.Ai => BuildAi(english),
		WizardStep.Guide => BuildGuide(english),
		_ => BuildReady(english),
	};

	/// <summary>释放形象立绘。之后再构建形象页只画空白占位。</summary>
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		foreach (Bitmap? bitmap in _thumbnails.Values) bitmap?.Dispose();
		_thumbnails.Clear();
	}

	// ── 欢迎 ───────────────────────────────────────────────────────────────

	private Control BuildWelcome(bool english)
	{
		string version = _services.Config.GetStringOr("app_version", "Dev");

		// 路线图直接取步骤标题：以后增删步骤，这里不会和顶部指示条说两套话。
		StackPanel route = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		int number = 1;
		foreach (WizardStep step in FirstRunWizard.Order)
		{
			if (step is WizardStep.Welcome or WizardStep.Ready) continue;
			route.Children.Add(RouteChip(number++, Title(step, english)));
		}

		return Stage(
			/*
			 * 使用 NoriHalo 而非静态标志位图。
			 *
			 * 本页紧接初始化窗口，后者刚完成 Dormant → Connected 的档位序列；
			 * 此处换成静态图会中断视觉连续性。取 Waking 档：已就绪但无进行中的任务，
			 * 与本步骤的语义一致。
			 */
			new Ui.NoriHalo(96) {Mood = Ui.HaloMood.Waking, HorizontalAlignment = HorizontalAlignment.Center},
			Heading(english ? "Nori Desktop Companion" : "Nori 桌面伴侣", NoriMetrics.Font2xl),
			Muted(english
				? "An AI companion on your desktop: she chats with you, remembers what you share, and can lend a hand once you allow it."
				: "住在桌面上的 AI 伙伴：陪你聊天，记得你说过的事，也能在你允许后帮你动手做事。", 400),
			new Panel {Height = 4},
			Faint(english ? "About a minute to get started:" : "接下来大约 1 分钟："),
			route,
			Faint(english ? "Every choice can be changed later in Settings." : "这里的选择之后都能在设置中修改。"),
			Faint(version));
	}

	/// <summary>欢迎页路线图上的一站：序号加步骤名。</summary>
	private static Control RouteChip(int number, string label) => new Border
	{
		Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
		CornerRadius = new CornerRadius(NoriMetrics.RadiusPill), Padding = new Thickness(6, 4, 12, 4),
		Child = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 6,
			Children =
			{
				new Border
				{
					Width = 18, Height = 18, CornerRadius = new CornerRadius(NoriMetrics.RadiusPill),
					Background = ChatPalette.Overlay,
					Child = new TextBlock
					{
						Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
						FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Teal,
						HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
					},
				},
				new TextBlock
				{
					Text = label, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Body,
					VerticalAlignment = VerticalAlignment.Center,
				},
			},
		},
	};

	// ── 语言 ───────────────────────────────────────────────────────────────

	private Control BuildLanguage(bool english)
	{
		string current = _services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN");
		StackPanel list = new() {Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center};

		foreach ((string code, string name, string sub) in new[]
		{
			("zh-CN", "简体中文", "Chinese (Simplified)"),
			("en-US", "English", "American English"),
		})
		{
			list.Children.Add(Choice(name, sub, code == current, () =>
			{
				_services.Config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(code));
				_services.Runtime?.InvalidateSnapshot();
				// 整幅重画：标题、按钮、以及这一页自己的文案都要跟着换。
				_onRebuild();
			}));
		}

		return Stage(
			Heading(english ? "Choose a language" : "选择语言", NoriMetrics.FontXl),
			Muted(english
				? "Used for the text in every Nori window. You can change it later in Settings → General."
				: "用于 Nori 所有窗口的界面文字，之后可在 设置 → 常规 中修改。", 360),
			list);
	}

	// ── 形象 ───────────────────────────────────────────────────────────────

	private Control BuildModel(bool english)
	{
		StackPanel list = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 12,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		string[] catalog = [.. SupportedModelIds.All];
		string configured = _services.Config.GetStringOr(ConfigStore.KeySelectedModel, "");

		List<string> installed = [.. catalog.Where(IsInstalled)];
		if (!installed.Contains(SelectedModel))
			SelectedModel = installed.Contains(configured) ? configured : installed.FirstOrDefault() ?? "";

		bool online = !_services.SafeMode;
		// 一个都没装时自动在线获取默认形象，只尝试一次；失败后由用户点选重试或改用本地导入。
		// 推到下一帧再开始 —— 在构建过程中回调重建会递归。
		if (installed.Count == 0 && online && !_autoDownloadTried && !IsImporting)
		{
			_autoDownloadTried = true;
			Dispatcher.UIThread.Post(() => _ = DownloadModelAsync(ConfigStore.DefaultModel, english));
		}

		foreach (string id in catalog)
		{
			bool ready = installed.Contains(id);
			string captured = id;
			list.Children.Add(Choice(
				BuiltInModels.Name(id),
				ready ? english ? "Installed" : "已安装"
				: _downloading == id ? english ? "Downloading..." : "正在下载..."
				: online ? english ? "Not installed · click to download" : "未安装 · 点击在线获取"
				: english ? "Not installed" : "未安装",
				id == SelectedModel,
				IsImporting ? null
				: ready
					? () =>
					{
						SelectedModel = captured;
						_importError = "";
						_onRebuild();
					}
				: online ? () => _ = DownloadModelAsync(captured, english)
				: null,
				Thumbnail(id), 250));
		}

		// 一个都没装就挡住：带着空配置进主界面，伴侣窗口会是一片空白。
		// 写 Gate 而不是回调 —— 构建过程中回调会递归。
		Gate = _downloading.Length > 0
			? (english ? $"Downloading {_downloading} from static.nori.mom..." : $"正在从 static.nori.mom 下载 {_downloading}...") + _downloadProgress
			: IsImporting
			? english ? "Importing appearance..." : "正在导入形象..."
			: _importError.Length > 0 ? _importError
			: SelectedModel.Length > 0 ? ""
			: english ? "Import an appearance to continue" : "先导入一个形象才能继续";

		StackPanel imports = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 12,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		foreach (string kind in new[] {"zip", "folder"})
		{
			Button import = new()
			{
				Name = kind == "zip" ? "FirstRunImportZip" : "FirstRunImportFolder",
				Content = kind == "zip"
					? english ? "Import ZIP" : "导入 ZIP"
					: english ? "Import folder" : "导入文件夹",
				Padding = new Thickness(16, 6), CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
				Background = ChatPalette.Panel, Foreground = ChatPalette.Body,
				BorderThickness = default, IsEnabled = !IsImporting,
			};
			import.Click += async (_, _) => await ImportModelAsync(kind, english);
			imports.Children.Add(import);
		}

		return Stage(
			Heading(english ? "Choose an appearance" : "选择形象", NoriMetrics.FontXl),
			Muted(online
				? english
					? "This is how Nori looks on your desktop. Appearances that aren't installed are downloaded from static.nori.mom; you can also import your own."
					: "这是 Nori 在桌面上的样子。未安装的形象会从 static.nori.mom 在线获取，也可以导入自己的模型。"
				: english
					? "Downloads are off in safe mode. Import a local Live2D model instead."
					: "安全模式下不在线获取，请导入本地 Live2D 模型。", 400),
			list,
			new Panel {Height = 2},
			imports,
			Faint(english
				? "Local import: a Live2D ZIP or folder that contains a .model3.json file."
				: "本地导入：选择包含 .model3.json 的 Live2D 模型 ZIP 或文件夹。"));
	}

	/// <summary>形象卡片左侧的立绘；取不到就留一块同尺寸的底色，卡片不跳动。</summary>
	private Control Thumbnail(string id)
	{
		if (!_thumbnails.TryGetValue(id, out Bitmap? bitmap) && !_disposed)
			_thumbnails[id] = bitmap = BuiltInModels.OpenThumbnail(id);
		return new Border
		{
			Width = 56, Height = 78, ClipToBounds = true,
			CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
			Background = ChatPalette.Overlay,
			Child = bitmap is null ? null : new Image {Source = bitmap, Stretch = Stretch.UniformToFill},
		};
	}

	/// <summary>
	/// 从 static.nori.mom 获取形象并选中。与本地导入共用 <see cref="IsImporting"/>，期间禁止离开当前步骤。
	/// </summary>
	internal async Task DownloadModelAsync(string modelId, bool english)
	{
		if (IsImporting || lifetimeToken.IsCancellationRequested || _services.SafeMode) return;
		IsImporting = true;
		_importError = "";
		_downloading = modelId;
		_downloadProgress = "";
		_onRebuild();
		try
		{
			Progress<RemoteModelProgress> progress = new(report =>
			{
				if (_downloading.Length == 0) return;
				_downloadProgress = $" {report.CompletedFiles}/{report.TotalFiles}";
				_onRebuild();
			});
			SelectedModel = await _services.InstallRemoteModelAsync(modelId, progress, lifetimeToken);
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
		catch (Exception failure)
		{
			_importError = (english ? "Download failed, retry or import locally: " : "在线获取失败，可重试或导入本地模型：") + failure.Message;
			_services.Logger.Write(LogSource.Backend, "warn", $"首次运行在线获取模型失败 [{modelId}]", exception: failure);
		}
		finally
		{
			IsImporting = false;
			_downloading = "";
			_onRebuild();
		}
	}

	/// <summary>直接复用资源服务的校验与原子导入，不借用其他窗口的桥接身份。</summary>
	internal async Task ImportModelAsync(string sourceKind, bool english)
	{
		if (IsImporting || lifetimeToken.IsCancellationRequested) return;
		IsImporting = true;
		_importError = "";
		_onRebuild();
		try
		{
			string? path = await pickModel(sourceKind).WaitAsync(lifetimeToken);
			if (string.IsNullOrWhiteSpace(path)) return;
			lifetimeToken.ThrowIfCancellationRequested();
			IReadOnlyList<string> imported = await Task.Run(
				() => _services.Resources.Import(ResourceType.Live2D, path, lifetimeToken),
				lifetimeToken);
			lifetimeToken.ThrowIfCancellationRequested();
			SelectedModel = imported.FirstOrDefault() ?? SelectedModel;
			_services.Runtime?.InvalidateSnapshot();
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
		catch (Exception failure)
		{
			_importError = (english ? "Import failed, please retry: " : "导入失败，请重试：") + failure.Message;
			_services.Logger.Write(LogSource.Backend, "warn", failure.InnerException is { } inner
				? $"首次运行导入模型失败（根因 {inner.GetType().Name} 0x{inner.HResult:X8}）"
				: "首次运行导入模型失败", exception: failure);
		}
		finally
		{
			IsImporting = false;
			_onRebuild();
		}
	}

	private bool IsInstalled(string modelId)
	{
		try
		{
			return _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"检查模型资源失败 [{modelId}]", exception: exception);
			return false;
		}
	}

	// ── 就绪 ───────────────────────────────────────────────────────────────

	private Control BuildReady(bool english)
	{
		bool available = _services.Telemetry.IsAvailable;
		if (!_telemetryInitialized)
		{
			TelemetryEnabled = available && _services.Config.GetTelemetryConsent() != TelemetryConsent.Denied;
			_telemetryInitialized = true;
		}
		TelemetryEnabled &= available;

		CheckBox telemetry = new()
		{
			Content = english ? "Send anonymous diagnostics" : "发送匿名诊断数据",
			IsChecked = TelemetryEnabled,
			IsEnabled = available,
			Foreground = ChatPalette.Body,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		telemetry.IsCheckedChanged += (_, _) => TelemetryEnabled = telemetry.IsChecked == true;

		string model = _draft.Model.Trim();
		Grid summary = new()
		{
			ColumnDefinitions = new ColumnDefinitions("Auto,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
			ColumnSpacing = 16, RowSpacing = 8,
		};
		SummaryRow(summary, 0, english ? "Language" : "界面语言", english ? "English" : "简体中文");
		SummaryRow(summary, 1, english ? "Appearance" : "形象", SelectedModel.Length > 0
			? BuiltInModels.Name(SelectedModel)
			: english ? "Not selected" : "未选择");
		SummaryRow(summary, 2, english ? "Model provider" : "模型服务", AiSaved
			? AiDraftDefaults.DisplayName(_draft.Provider) + (model.Length > 0 ? " · " + model : "")
			: english ? "Not configured" : "未配置");

		List<Control> children =
		[
			Logo(56),
			Heading(english ? "You're all set" : "准备好了", NoriMetrics.FontXl),
			new Border
			{
				Width = 380, Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line,
				BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(NoriMetrics.RadiusLg),
				Padding = new Thickness(16, 12), HorizontalAlignment = HorizontalAlignment.Center,
				Child = summary,
			},
		];
		// 没配模型服务时，进主界面后快捷聊天只会显示「请先配置 AI」。在这里先说清楚
		// 后果和去哪配，不要等用户发出第一句话才发现。
		if (!AiSaved)
		{
			children.Add(new TextBlock
			{
				Name = "FirstRunAiMissing",
				Text = english
					? "Without a model provider Nori can't reply yet. Go Back to add one, or set it up later in Settings → Model providers."
					: "还没有配置模型服务，Nori 暂时不能回复你。可以点「上一步」回去填写，也可以之后在 设置 → 模型服务 中配置。",
				FontSize = NoriMetrics.FontXs, Foreground = NoriThemeTokens.Brush("warning"),
				TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
				TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
			});
		}
		children.Add(telemetry);
		children.Add(Faint(available
			? english
				? "Only redacted crash and performance data is sent — never chats, memories, voice, API keys or identity. Change it in Settings → General."
				: "只发送脱敏后的崩溃与性能信息，不含聊天内容、记忆、语音、API 密钥或身份信息。可在 设置 → 常规 中修改。"
			: english ? "Diagnostics are unavailable in this build." : "此版本不提供诊断上报。", 400));
		children.Add(Muted(_services.SafeMode
			? english
				? "Start opens the main window. Nori isn't summoned automatically in safe mode."
				: "点「开始使用」后会打开主界面。安全模式下不会自动唤出 Nori。"
			: english
				? "Start opens the main window and brings Nori to your desktop."
				: "点「开始使用」后会打开主界面，Nori 也会出现在桌面上。", 400));
		return Stage([.. children]);
	}

	private static void SummaryRow(Grid grid, int row, string label, string value)
	{
		TextBlock name = new() {Text = label, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Faint, VerticalAlignment = VerticalAlignment.Center};
		TextBlock content = new()
		{
			Text = value, FontSize = NoriMetrics.FontSm, Foreground = ChatPalette.Primary,
			TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
		};
		ToolTip.SetTip(content, value);
		Grid.SetRow(name, row);
		Grid.SetRow(content, row);
		Grid.SetColumn(content, 1);
		grid.Children.Add(name);
		grid.Children.Add(content);
	}

	// ── 小构件 ─────────────────────────────────────────────────────────────

	private static Control Stage(params Control[] children)
	{
		StackPanel body = new()
		{
			Spacing = 12,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		};
		foreach (Control child in children) body.Children.Add(child);
		// 内容比舞台高时要能滚，别把「下一步」需要看到的东西顶出可视区域。
		return new ScrollViewer
		{
			Name = StageName,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			Content = body,
		};
	}

	private static TextBlock Heading(string text, double size) => new()
	{
		Text = text, FontSize = size, FontWeight = FontWeight.SemiBold,
		Foreground = ChatPalette.Primary,
		HorizontalAlignment = HorizontalAlignment.Center,
	};

	private static TextBlock Muted(string text, double maxWidth) => new()
	{
		Text = text, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Muted,
		TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
		TextAlignment = TextAlignment.Center,
		HorizontalAlignment = HorizontalAlignment.Center,
	};

	/// <summary>次要说明：比 <see cref="Muted"/> 再淡一档，用于补充和提示。</summary>
	private static TextBlock Faint(string text, double maxWidth = 400) => new()
	{
		Text = text, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Faint,
		TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
		TextAlignment = TextAlignment.Center,
		HorizontalAlignment = HorizontalAlignment.Center,
	};

	private static Control Field(string label, Control editor)
	{
		AutomationProperties.SetName(editor, label);
		return new StackPanel
		{
			Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
			Children =
			{
				new TextBlock {Text = label, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Faint},
				editor,
			},
		};
	}

	/// <summary>
	/// 一张可选卡片。<paramref name="onPick"/> 为 null 表示这一项不可选；
	/// <paramref name="leading"/> 画在文字左侧（形象立绘）。
	/// </summary>
	private static Control Choice(string title, string subtitle, bool selected, Action? onPick,
		Control? leading = null, double width = 320)
	{
		StackPanel text = new()
		{
			Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				new TextBlock {Text = title, FontSize = NoriMetrics.FontBase, FontWeight = FontWeight.SemiBold, Foreground = ChatPalette.Primary},
				new TextBlock {Text = subtitle, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Faint, TextWrapping = TextWrapping.Wrap},
			},
		};
		Control content = text;
		if (leading is not null)
		{
			Grid row = new() {ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12};
			Grid.SetColumn(text, 1);
			row.Children.Add(leading);
			row.Children.Add(text);
			content = row;
		}

		Button card = new()
		{
			Width = width,
			Padding = new Thickness(14, 10),
			CornerRadius = new CornerRadius(NoriMetrics.RadiusLg),
			Background = selected ? ChatPalette.Panel : ChatPalette.Deep,
			BorderBrush = selected ? ChatPalette.Teal : ChatPalette.Faint,
			BorderThickness = new Thickness(selected ? 2 : 1),
			Opacity = onPick is null ? 0.45 : 1,
			IsEnabled = onPick is not null,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Content = content,
		};
		AutomationProperties.SetName(card, $"{title}. {subtitle}");
		if (onPick is not null) card.Click += (_, _) => onPick();
		return card;
	}

	/// <summary>标志图。取不到就不画 —— 缺一张图不该让向导走不下去。</summary>
	private static Control Logo(double size)
	{
		try
		{
			return new Image
			{
				Width = size, Height = size, Stretch = Stretch.Uniform,
				Source = new Bitmap(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/logo.png"))),
				HorizontalAlignment = HorizontalAlignment.Center,
			};
		}
		catch
		{
			return new Panel {Height = 0};
		}
	}
}
