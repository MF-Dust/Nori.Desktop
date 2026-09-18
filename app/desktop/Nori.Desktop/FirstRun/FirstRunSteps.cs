using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;

namespace Nori.Desktop.FirstRun;

/// <summary>
/// 向导五步各自的样子和各自要调的服务。
///
/// 壳（<c>FirstRunWindow</c>）只管步进和导航，不知道每一步在做什么；步进规则在
/// <see cref="FirstRunWizard"/>。三层分开是为了让规则能脱离 UI 单测。
///
/// 每一步通过 <c>onStepChanged</c> 把「我这一步现在能不能过」抬给壳：给一句话就是
/// 挡住并显示它，给空串就是解除。选形象那一步靠它挡住「一个形象都没有就往下走」。
/// </summary>
public sealed class FirstRunSteps(AppServices services, Action<string> onGate, Action onRebuild)
{
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

	/// <summary>选中的形象 id。空串表示一个都没装。</summary>
	public string SelectedModel { get; private set; } = "";

	/// <summary>用户在末步选的遥测开关。</summary>
	public bool TelemetryEnabled { get; private set; } = true;

	/// <summary>AI 那一步实际存进去了没有。末步的摘要据此说话。</summary>
	public bool AiSaved { get; private set; }

	/// <summary>指定选中的形象。**只给测试用** —— 测试环境里一个模型都没装。</summary>
	internal void SelectModelForTests(string modelId) => SelectedModel = modelId;

	/// <summary>步骤标题，画在顶部指示条旁边。</summary>
	public static string Title(WizardStep step, bool english) => step switch
	{
		WizardStep.Welcome => english ? "Welcome" : "欢迎",
		WizardStep.Language => english ? "Language" : "语言",
		WizardStep.Model => english ? "Appearance" : "形象",
		WizardStep.Ai => english ? "Model provider" : "模型服务",
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
		_ => BuildReady(english),
	};

	// ── 欢迎 ───────────────────────────────────────────────────────────────

	private Control BuildWelcome(bool english)
	{
		string version = _services.Config.GetStringOr("app_version", "Dev");
		StackPanel body = new()
		{
			Spacing = 14,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				Logo(84),
				Heading(english ? "Nori is here" : "Nori 来了", 22),
				Muted(english
					? "A desktop companion that talks, remembers, and can use your tools."
					: "一个会说话、会记事、也能动手用工具的桌面伴侣。", 340),
				new TextBlock
				{
					Text = version, Foreground = ChatPalette.Faint, FontSize = 11,
					HorizontalAlignment = HorizontalAlignment.Center,
				},
			},
		};
		return body;
	}

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
				_services.Runtime?.InvalidateSnapshot("general");
				// 整幅重画：标题、按钮、以及这一页自己的文案都要跟着换。
				_onRebuild();
			}));
		}

		return Stage(
			Heading(english ? "Choose a language" : "选择语言", 19),
			Muted(english ? "You can change this later in Settings." : "之后可以在设置里改。", 320),
			list);
	}

	// ── 形象 ───────────────────────────────────────────────────────────────

	private Control BuildModel(bool english)
	{
		StackPanel list = new() {Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center};
		string[] catalog = [.. SupportedModelIds.All];
		string configured = _services.Config.GetStringOr(ConfigStore.KeySelectedModel, "");

		List<string> installed = [.. catalog.Where(IsInstalled)];
		if (SelectedModel.Length == 0)
			SelectedModel = installed.Contains(configured) ? configured : installed.FirstOrDefault() ?? "";

		foreach (string id in catalog)
		{
			bool ready = installed.Contains(id);
			string captured = id;
			list.Children.Add(Choice(
				id,
				ready ? english ? "Installed" : "已安装" : english ? "Not installed" : "未安装",
				id == SelectedModel,
				ready
					? () =>
					{
						SelectedModel = captured;
						_onRebuild();
					}
					: null));
		}

		// 一个都没装就挡住：带着空配置进主界面，伴侣窗口会是一片空白。
		// 写 Gate 而不是回调 —— 构建过程中回调会递归。
		Gate = SelectedModel.Length > 0
			? ""
			: english ? "Import an appearance to continue" : "先导入一个形象才能继续";

		return Stage(
			Heading(english ? "Choose an appearance" : "选择形象", 19),
			Muted(english
				? "Only installed appearances can be selected. More can be added later in Models."
				: "只有已安装的形象可以选。之后可以在模型窗口里添加。", 340),
			list);
	}

	private bool IsInstalled(string modelId)
	{
		try
		{
			return _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"检查模型资源失败 [{modelId}]: {exception.Message}");
			return false;
		}
	}

	// ── 模型服务 ───────────────────────────────────────────────────────────

	private Control BuildAi(bool english)
	{
		ComboBox provider = new()
		{
			Width = 320,
			ItemsSource = AiDraftDefaults.Providers,
			SelectedItem = _draft.Provider,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		TextBox baseUrl = new()
		{
			Width = 320, Text = _draft.BaseUrl,
			PlaceholderText = AiDraftDefaults.DefaultBaseUrl(_draft.Provider),
		};
		TextBox apiKey = new()
		{
			Width = 320, Text = _draft.ApiKey,
			PasswordChar = '•',
			PlaceholderText = AiDraftDefaults.ApiKeyHint(_draft.Provider),
		};
		TextBox model = new()
		{
			Width = 320, Text = _draft.Model,
			PlaceholderText = english ? "Model name" : "模型名",
		};
		TextBlock result = new()
		{
			Foreground = ChatPalette.Muted, FontSize = 11,
			HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false,
			TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
		};

		void Sync()
		{
			_draft = new AiDraft
			{
				Provider = provider.SelectedItem as string ?? AiDraftDefaults.OpenAi,
				BaseUrl = baseUrl.Text ?? "",
				ApiKey = apiKey.Text ?? "",
				Model = model.Text ?? "",
			};
			// 只刷底部，**不能重建舞台** —— 重建会把输入框换掉，焦点和光标当场丢。
			_onGate("");
		}

		provider.SelectionChanged += (_, _) =>
		{
			baseUrl.PlaceholderText = AiDraftDefaults.DefaultBaseUrl(provider.SelectedItem as string ?? AiDraftDefaults.OpenAi);
			apiKey.PlaceholderText = AiDraftDefaults.ApiKeyHint(provider.SelectedItem as string ?? AiDraftDefaults.OpenAi);
			Sync();
		};
		baseUrl.TextChanged += (_, _) => Sync();
		apiKey.TextChanged += (_, _) => Sync();
		model.TextChanged += (_, _) => Sync();

		Button fetch = new()
		{
			Content = english ? "Fetch models" : "获取模型",
			Padding = new Thickness(16, 6), CornerRadius = new CornerRadius(8),
			Background = ChatPalette.Panel, Foreground = ChatPalette.Body,
			BorderThickness = default,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		fetch.Click += async (_, _) =>
		{
			// 这一步用「获取模型」验证地址与密钥：拉得到列表就说明这套凭据是通的。
			// 不用连接测试 —— 那条命令的授权面只给主窗口。
			fetch.IsEnabled = false;
			result.IsVisible = true;
			result.Foreground = ChatPalette.Muted;
			result.Text = english ? "Fetching..." : "正在获取...";
			try
			{
				IReadOnlyList<string> names = await _services.Llm.FetchModelsAsync(
					_draft.Provider, AiDraftDefaults.EffectiveBaseUrl(_draft), _draft.ApiKey.Trim());
				result.Foreground = ChatPalette.Teal;
				result.Text = english
					? $"{names.Count} models available"
					: $"拉到 {names.Count} 个模型";
				if (model.Text is null or "" && names.Count > 0) model.Text = names[0];
			}
			catch (Exception failure)
			{
				result.Foreground = ChatPalette.Danger;
				result.Text = failure.Message;
			}
			finally
			{
				fetch.IsEnabled = true;
			}
		};

		Button skip = new()
		{
			Content = english ? "Skip for now" : "暂时跳过",
			Background = Brushes.Transparent, Foreground = ChatPalette.Faint,
			BorderThickness = default, FontSize = 11,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		skip.Click += (_, _) =>
		{
			// 清空草稿再让壳前进：跳过就是真的什么都不存。
			_draft = new AiDraft();
			AiSaved = false;
			baseUrl.Text = apiKey.Text = model.Text = "";
			_onGate("");
		};

		return Stage(
			Heading(english ? "Connect a model provider" : "接入模型服务", 19),
			Muted(english
				? "This step is optional — you can fill it in later in Settings."
				: "这一步可以跳过，之后在设置里补也行。", 340),
			Field(english ? "Protocol" : "协议", provider),
			Field(english ? "API base URL" : "接口地址", baseUrl),
			Field(english ? "API key" : "密钥", apiKey),
			Field(english ? "Model" : "模型", model),
			new StackPanel
			{
				Orientation = Orientation.Horizontal, Spacing = 12,
				HorizontalAlignment = HorizontalAlignment.Center,
				Children = {fetch, skip},
			},
			result);
	}

	/// <summary>
	/// 离开 AI 那一步时落盘。
	///
	/// 没填东西就什么都不做并返回成功 —— 这一步本来就可以跳过。填了就存，存失败
	/// 返回 false，由壳挡在原地。
	/// </summary>
	public async Task<bool> SaveAiDraftAsync()
	{
		AiSaved = false;
		if (AiDraftDefaults.BuildPatch(_draft) is not { } patch) return true;
		try
		{
			_services.AiSettings.UpdateChat(new AiChatSettingsPatch(
				Provider: patch.Provider,
				BaseUrl: patch.BaseUrl,
				ApiKey: patch.ApiKey,
				Model: patch.Model,
				ApiKeySpecified: patch.ApiKey is not null));
			_services.Runtime?.InvalidateSnapshot("ai");
			AiSaved = true;
			return true;
		}
		catch (Exception failure)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"首次运行保存模型服务失败：{failure.GetType().Name}");
			return false;
		}
	}

	// ── 就绪 ───────────────────────────────────────────────────────────────

	private Control BuildReady(bool english)
	{
		bool available = _services.Telemetry.IsAvailable;
		TelemetryEnabled = available && _services.Config.GetTelemetryConsent() != TelemetryConsent.Denied;

		CheckBox telemetry = new()
		{
			Content = english ? "Send anonymous diagnostics" : "发送匿名诊断数据",
			IsChecked = TelemetryEnabled,
			IsEnabled = available,
			Foreground = ChatPalette.Body,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		telemetry.IsCheckedChanged += (_, _) => TelemetryEnabled = telemetry.IsChecked == true;

		string aiLine = AiSaved
			? english ? "Model provider: configured" : "模型服务：已接入"
			: english ? "Model provider: can be added later in Settings" : "模型服务：之后可在设置里补";
		string modelLine = SelectedModel.Length > 0
			? (english ? "Appearance: " : "形象：") + SelectedModel
			: english ? "Appearance: none" : "形象：未选";

		return Stage(
			Logo(64),
			Heading(english ? "All set" : "准备好了", 20),
			Muted(modelLine, 340),
			Muted(aiLine, 340),
			telemetry,
			new TextBlock
			{
				Text = available
					? english ? "You can change this any time in Settings." : "随时可以在设置里改。"
					: english ? "Diagnostics are unavailable in this build." : "此版本不提供诊断上报。",
				Foreground = ChatPalette.Faint, FontSize = 11,
				HorizontalAlignment = HorizontalAlignment.Center,
			});
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
		Text = text, FontSize = 12, Foreground = ChatPalette.Muted,
		TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
		TextAlignment = TextAlignment.Center,
		HorizontalAlignment = HorizontalAlignment.Center,
	};

	private static Control Field(string label, Control editor) => new StackPanel
	{
		Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
		Children =
		{
			new TextBlock {Text = label, FontSize = 11, Foreground = ChatPalette.Faint},
			editor,
		},
	};

	/// <summary>一张可选卡片。<paramref name="onPick"/> 为 null 表示这一项不可选。</summary>
	private static Control Choice(string title, string subtitle, bool selected, Action? onPick)
	{
		Border card = new()
		{
			Width = 320,
			Padding = new Thickness(14, 10),
			CornerRadius = new CornerRadius(10),
			Background = selected ? ChatPalette.Panel : ChatPalette.Deep,
			BorderBrush = selected ? ChatPalette.Teal : ChatPalette.Faint,
			BorderThickness = new Thickness(selected ? 2 : 1),
			Opacity = onPick is null ? 0.45 : 1,
			Cursor = onPick is null ? null : new Cursor(StandardCursorType.Hand),
			Child = new StackPanel
			{
				Spacing = 2,
				Children =
				{
					new TextBlock {Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = ChatPalette.Primary},
					new TextBlock {Text = subtitle, FontSize = 11, Foreground = ChatPalette.Faint},
				},
			},
		};
		if (onPick is not null) card.PointerPressed += (_, _) => onPick();
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
