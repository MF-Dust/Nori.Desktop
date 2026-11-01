using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Core.Configuration;
using Nori.Core.FirstRun;
using Nori.Core.Logging;
using Nori.Desktop.Appearance;
using Nori.Desktop.Chat;

namespace Nori.Desktop.FirstRun;

/// <summary>
/// 模型服务一步：左边填表，右边讲清楚每一项填什么、密钥放在哪、跳过会怎样。
///
/// 用户反馈「装好了不知道怎么用」，其中一大块是这一步看不懂就跳过了，进主界面后
/// 发的消息没人回。所以右侧说明随协议切换，跳过的后果写在标题下面。
/// </summary>
public sealed partial class FirstRunSteps
{
	private Control BuildAi(bool english)
	{
		IReadOnlyList<string> providers = AiDraftDefaults.Providers;
		ComboBox provider = new()
		{
			Width = 320,
			ItemsSource = providers.Select(AiDraftDefaults.DisplayName).ToList(),
			SelectedIndex = Math.Max(0, providers.ToList().IndexOf(_draft.Provider)),
		};
		string SelectedProvider() =>
			provider.SelectedIndex >= 0 && provider.SelectedIndex < providers.Count
				? providers[provider.SelectedIndex]
				: AiDraftDefaults.OpenAi;

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
			PlaceholderText = english ? "Model name, e.g. from Fetch models" : "模型名，可用「获取模型」自动填入",
		};
		// 拉到列表之后才出现：用户多半不知道该填哪个模型名，给他一个能点的列表。
		ComboBox picker = new()
		{
			Name = "FirstRunModelPicker",
			Width = 320, IsVisible = false, MaxDropDownHeight = 240,
			PlaceholderText = english ? "Pick from fetched models" : "从获取到的模型中选择",
		};
		TextBlock result = new()
		{
			Foreground = ChatPalette.Muted, FontSize = NoriMetrics.FontXs,
			IsVisible = false, TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
		};
		TextBlock providerTitle = HelpTitle(AiDraftDefaults.DisplayName(_draft.Provider));
		TextBlock providerNote = HelpBody(ProviderNote(_draft.Provider, english));

		void Sync()
		{
			_draft = new AiDraft
			{
				Provider = SelectedProvider(),
				BaseUrl = baseUrl.Text ?? "",
				ApiKey = apiKey.Text ?? "",
				Model = model.Text ?? "",
			};
			// 只刷底部，**不能重建舞台** —— 重建会把输入框换掉，焦点和光标当场丢。
			_onGate("");
		}

		void HidePicker()
		{
			picker.IsVisible = false;
			picker.ItemsSource = null;
		}

		provider.SelectionChanged += (_, _) =>
		{
			string id = SelectedProvider();
			baseUrl.PlaceholderText = AiDraftDefaults.DefaultBaseUrl(id);
			apiKey.PlaceholderText = AiDraftDefaults.ApiKeyHint(id);
			providerTitle.Text = AiDraftDefaults.DisplayName(id);
			providerNote.Text = ProviderNote(id, english);
			// 换了协议，之前拉到的列表就不再对应这一套地址和密钥。
			HidePicker();
			Sync();
		};
		baseUrl.TextChanged += (_, _) => Sync();
		apiKey.TextChanged += (_, _) => Sync();
		model.TextChanged += (_, _) => Sync();
		picker.SelectionChanged += (_, _) =>
		{
			if (picker.SelectedItem is string name && name != model.Text) model.Text = name;
		};

		Button fetch = new()
		{
			Content = english ? "Fetch models" : "获取模型",
			Padding = new Thickness(16, 6), CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
			Background = ChatPalette.Panel, Foreground = ChatPalette.Body,
			BorderThickness = default,
		};
		fetch.Click += async (_, _) =>
		{
			// 这一步用「获取模型」验证地址与密钥：拉得到列表就说明这套凭据是通的。
			// 不用连接测试 —— 那条命令的授权面只给主窗口。
			fetch.IsEnabled = false;
			result.IsVisible = true;
			result.Foreground = ChatPalette.Muted;
			result.Text = english ? "Fetching…" : "正在获取…";
			try
			{
				IReadOnlyList<string> names = await _services.Llm.FetchModelsAsync(
					_draft.Provider, AiDraftDefaults.EffectiveBaseUrl(_draft), _draft.ApiKey.Trim());
				result.Foreground = ChatPalette.Teal;
				result.Text = english
					? $"Connected · {names.Count} models available"
					: $"连接成功 · 已获取 {names.Count} 个模型";
				if (model.Text is null or "" && names.Count > 0) model.Text = names[0];
				picker.ItemsSource = names;
				picker.SelectedItem = names.Contains(model.Text ?? "") ? model.Text : null;
				picker.IsVisible = names.Count > 0;
			}
			catch (Exception failure)
			{
				HidePicker();
				result.Foreground = ChatPalette.Danger;
				result.Text = (english ? "Couldn't connect: " : "连接失败：") + failure.Message;
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
			BorderThickness = default, FontSize = NoriMetrics.FontXs,
			VerticalAlignment = VerticalAlignment.Center,
		};
		skip.Click += (_, _) =>
		{
			// 清空草稿再让壳前进：跳过就是真的什么都不存。
			_draft = new AiDraft();
			AiSaved = false;
			baseUrl.Text = apiKey.Text = model.Text = "";
			HidePicker();
			result.IsVisible = false;
			_onGate("");
		};

		StackPanel form = new()
		{
			// 间距收紧到 6：拉到模型列表后会多出选择框和结果行，默认窗口里仍要尽量一屏放下。
			Width = 320, Spacing = 6,
			Children =
			{
				Field(english ? "Protocol" : "协议", provider),
				Field(english ? "API base URL (optional)" : "接口地址（可留空）", baseUrl),
				Field(english ? "API key" : "密钥", apiKey),
				Field(english ? "Model" : "模型", model),
				picker,
				new StackPanel
				{
					Orientation = Orientation.Horizontal, Spacing = 8,
					Children = {fetch, skip},
				},
				result,
			},
		};
		Border help = new()
		{
			Name = "FirstRunAiHelp",
			Width = 340, VerticalAlignment = VerticalAlignment.Top,
			Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(NoriMetrics.RadiusLg), Padding = new Thickness(16, 14),
			Child = new StackPanel
			{
				Spacing = 12,
				Children =
				{
					HelpBlock(providerTitle, providerNote),
					HelpBlock(HelpTitle(english ? "What you need" : "需要准备"), HelpBody(english
						? "An API key created in your provider's console, and the model you want to use. Leave the base URL empty for the official endpoint; fill it in for a proxy or compatible service."
						: "在服务商控制台创建的 API 密钥，以及要使用的模型名。接口地址留空即使用官方地址，用代理或兼容服务时再填写。")),
					HelpBlock(HelpTitle(english ? "Check it works" : "检查一下"), HelpBody(english
						? "Fetch models verifies the URL and key, then lets you pick a model from the list."
						: "点「获取模型」验证地址和密钥，成功后可以直接从列表里选模型。")),
					HelpBlock(HelpTitle(english ? "About your key" : "关于密钥"), HelpBody(english
						? "Your key is encrypted on this computer and never synced to the cloud."
						: "密钥加密保存在这台电脑上，不会同步到云端。")),
				},
			},
		};
		Grid columns = new()
		{
			ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), ColumnSpacing = 24,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		Grid.SetColumn(help, 1);
		columns.Children.Add(form);
		columns.Children.Add(help);

		return Stage(
			Heading(english ? "Connect a model provider" : "连接模型服务", NoriMetrics.FontXl),
			Muted(english
				? "Optional, but Nori can't reply until a provider is set up. You can also do this later in Settings."
				: "可以跳过，但配置之前 Nori 无法回复消息；之后也能在设置中配置。", 520),
			columns);
	}

	/// <summary>右侧说明的第一段：随协议切换，告诉用户「我该不该选这个」。</summary>
	private static string ProviderNote(string provider, bool english) => provider switch
	{
		AiDraftDefaults.OpenAi => english
			? "For OpenAI and OpenAI-compatible services such as DeepSeek or OpenRouter. Compatible services need their own base URL."
			: "OpenAI 官方，以及 DeepSeek、OpenRouter 等兼容 OpenAI 接口的服务都选它；兼容服务需要填写自己的接口地址。",
		AiDraftDefaults.OpenAiResponses => english
			? "OpenAI's newer Responses API for the official OpenAI service. If unsure, choose Chat Completions."
			: "OpenAI 官方的新版 Responses 接口。不确定时选 Chat Completions。",
		AiDraftDefaults.Anthropic => english
			? "Anthropic's official Claude API. Keys usually start with sk-ant-."
			: "Anthropic 官方 Claude 接口，密钥通常以 sk-ant- 开头。",
		AiDraftDefaults.Google => english
			? "Google Gemini API, with a key created in Google AI Studio."
			: "Google Gemini 接口，使用在 Google AI Studio 创建的密钥。",
		_ => "",
	};

	private static TextBlock HelpTitle(string text) => new()
	{
		Text = text, FontSize = NoriMetrics.FontSm, FontWeight = FontWeight.SemiBold, Foreground = ChatPalette.Primary,
	};

	private static TextBlock HelpBody(string text) => new()
	{
		Text = text, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Muted, TextWrapping = TextWrapping.Wrap,
	};

	private static Control HelpBlock(TextBlock title, TextBlock body) =>
		new StackPanel {Spacing = 4, Children = {title, body}};

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
			_services.Runtime?.InvalidateSnapshot();
			AiSaved = true;
			return true;
		}
		catch (Exception failure)
		{
			_services.Logger.Write(LogSource.Backend, "warn", "首次运行保存模型服务失败", exception: failure);
			return false;
		}
	}
}
