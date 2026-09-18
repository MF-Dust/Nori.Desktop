using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Main;

/// <summary>
/// 主界面的首页。
///
/// 内容与 Vue 版一致：出事的横幅在最上面、伴侣状态与两个操作、三块运行概况、
/// 几个去别处的入口。顺序不是随手排的 —— 需要用户处理的东西（模型没装、安全模式）
/// 必须排在她的近况之前，否则用户会先看一眼状态就关窗。
/// </summary>
public sealed class HomeView : Panel
{
	private readonly AppServices _services;
	private readonly Action _onChanged;
	private readonly StackPanel _body = new() {Spacing = 16};

	public HomeView(AppServices services, Action onChanged)
	{
		_services = services;
		_onChanged = onChanged;
		Children.Add(_body);
	}

	/// <summary>整幅重画。这一页很轻，重建比维护一堆通知源便宜。</summary>
	public void Refresh(bool english)
	{
		_body.Children.Clear();

		string modelId = _services.Config.GetStringOr(ConfigStore.KeySelectedModel, ConfigStore.DefaultModel);
		bool modelReady = IsInstalled(modelId);
		bool petVisible = _services.Windows?.IsWindowVisible(WindowLabels.Pet) ?? false;

		// ── 要你处理的事，排在最前 ──────────────────────────────────────
		if (!modelReady)
		{
			_body.Children.Add(Banner(
				english ? "No appearance installed" : "还没有装形象",
				english
					? "Nori cannot appear on the desktop until an appearance is installed."
					: "没有形象，Nori 就没法出现在桌面上。",
				english ? "Open Models" : "去模型窗口",
				() => _services.Windows?.Show(WindowLabels.Models),
				ChatPalette.Danger));
		}
		if (_services.SafeMode)
		{
			_body.Children.Add(Banner(
				english ? "Safe mode" : "安全模式",
				english
					? "Tools, automation and plugins are all off. Restart normally to use them."
					: "工具、自动化与插件全部关闭。要用它们请正常重启。",
				null, null, ChatPalette.Accent));
		}

		// ── 她现在怎么样 ────────────────────────────────────────────────
		_body.Children.Add(Hero(english, modelId, modelReady, petVisible));

		// ── 运行概况 ────────────────────────────────────────────────────
		_body.Children.Add(SectionTitle(english ? "At a glance" : "运行概况"));
		_body.Children.Add(new WrapPanel
		{
			Children =
			{
				Tile(english ? "Model provider" : "模型服务", ProviderState(english), ProviderDetail()),
				Tile(english ? "Skills on" : "启用技能", SkillCount().ToString(), english ? "Toggle them in Settings" : "可在技能设置中开关"),
				Tile(english ? "Tools" : "可用工具", ToolCount().ToString(), english ? "Built-in plus MCP" : "含内置工具与 MCP 工具"),
			},
		});

		// ── 去别处 ──────────────────────────────────────────────────────
		_body.Children.Add(SectionTitle(english ? "Go to" : "快速前往"));
		_body.Children.Add(new WrapPanel
		{
			Children =
			{
				Card(english ? "Chat" : "对话",
					english ? "Talk to Nori about anything." : "随时随地与 Nori 聊各种话题。",
					() => _services.Windows?.Show(WindowLabels.Chat)),
				Card(english ? "Models" : "模型换装",
					english ? "Switch appearance and expression packs." : "切换造型外观与预设表情包。",
					() => _services.Windows?.Show(WindowLabels.Models)),
				Card(english ? "Memory" : "记忆",
					english ? "See and edit what she remembers." : "查看并编辑她记住的事。",
					() => _services.Windows?.Show(WindowLabels.Memory)),
				Card(english ? "Settings" : "设置",
					english ? "Model provider, permissions, voice." : "模型服务、权限与能力、语音。",
					() => _services.Windows?.Show(WindowLabels.Settings)),
			},
		});
	}

	// ── 各块 ───────────────────────────────────────────────────────────────

	private Control Hero(bool english, string modelId, bool modelReady, bool petVisible)
	{
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 10};

		if (modelReady)
		{
			actions.Children.Add(Primary(
				petVisible
					? english ? "Hide Nori" : "收起 Nori"
					: english ? "Summon Nori" : "召唤 Nori",
				() =>
				{
					if (petVisible) _services.Windows?.Hide(WindowLabels.Pet);
					else _services.Windows?.Show(WindowLabels.Pet);
					_onChanged();
				}));
			actions.Children.Add(Secondary(english ? "Wave" : "让她打个招呼", () =>
			{
				try { _services.PetRuntime?.PlayMotionByName("wave"); }
				catch (Exception failure)
				{
					_services.Logger.Write(LogSource.Backend, "warn", $"播放动作失败：{failure.GetType().Name}");
				}
			}));
		}
		else
		{
			actions.Children.Add(Primary(english ? "Import an appearance" : "导入形象",
				() => _services.Windows?.Show(WindowLabels.Models)));
		}

		return new Border
		{
			Background = ChatPalette.Panel,
			CornerRadius = new CornerRadius(14),
			Padding = new Thickness(20, 18),
			Child = new StackPanel
			{
				Orientation = Orientation.Horizontal, Spacing = 18,
				Children =
				{
					Avatar(modelId),
					new StackPanel
					{
						Spacing = 6,
						VerticalAlignment = VerticalAlignment.Center,
						Children =
						{
							new TextBlock
							{
								Text = modelId, FontSize = 19, FontWeight = FontWeight.SemiBold,
								Foreground = ChatPalette.Primary,
							},
							new TextBlock
							{
								Text = petVisible
									? english ? "On your desktop" : "在桌面上"
									: english ? "Not on the desktop" : "没有出现在桌面上",
								FontSize = 12, Foreground = petVisible ? ChatPalette.Teal : ChatPalette.Faint,
							},
							actions,
						},
					},
				},
			},
		};
	}

	private static Control SectionTitle(string text) => new TextBlock
	{
		Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold,
		Foreground = ChatPalette.Muted, Margin = new Thickness(2, 6, 0, 0),
	};

	private static Control Tile(string label, string value, string note) => new Border
	{
		Width = 208,
		Margin = new Thickness(0, 0, 12, 12),
		Background = ChatPalette.Panel,
		CornerRadius = new CornerRadius(12),
		Padding = new Thickness(16, 14),
		Child = new StackPanel
		{
			Spacing = 4,
			Children =
			{
				new TextBlock {Text = label, FontSize = 11, Foreground = ChatPalette.Faint},
				new TextBlock
				{
					Text = value, FontSize = 20, FontWeight = FontWeight.SemiBold,
					Foreground = ChatPalette.Accent,
				},
				new TextBlock
				{
					Text = note, FontSize = 11, Foreground = ChatPalette.Faint,
					TextWrapping = TextWrapping.Wrap,
				},
			},
		},
	};

	private static Control Card(string title, string description, Action onOpen)
	{
		Border card = new()
		{
			Width = 268,
			Margin = new Thickness(0, 0, 12, 12),
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Faint, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(12),
			Padding = new Thickness(16, 14),
			Cursor = new Cursor(StandardCursorType.Hand),
			Child = new StackPanel
			{
				Spacing = 5,
				Children =
				{
					new TextBlock
					{
						Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold,
						Foreground = ChatPalette.Primary,
					},
					new TextBlock
					{
						Text = description, FontSize = 11, Foreground = ChatPalette.Faint,
						TextWrapping = TextWrapping.Wrap,
					},
				},
			},
		};
		card.PointerPressed += (_, _) => onOpen();
		return card;
	}

	/// <summary>需要处理的事。有动作就带一个按钮，没有就只说明白发生了什么。</summary>
	private static Control Banner(string title, string description, string? action, Action? onAction, IBrush accent)
	{
		StackPanel content = new()
		{
			Spacing = 5,
			Children =
			{
				new TextBlock
				{
					Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold,
					Foreground = ChatPalette.Primary,
				},
				new TextBlock
				{
					Text = description, FontSize = 11, Foreground = ChatPalette.Body,
					TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
				},
			},
		};
		if (action is not null && onAction is not null) content.Children.Add(Secondary(action, onAction));

		return new Border
		{
			Background = ChatPalette.Deep,
			BorderBrush = accent, BorderThickness = new Thickness(0, 0, 0, 2),
			CornerRadius = new CornerRadius(10),
			Padding = new Thickness(16, 12),
			Child = content,
		};
	}

	private static Button Primary(string text, Action onClick)
	{
		Button button = new()
		{
			Content = text,
			Padding = new Thickness(18, 7), CornerRadius = new CornerRadius(8),
			Background = ChatPalette.Teal, Foreground = ChatPalette.OnTeal,
			BorderThickness = default,
		};
		button.Click += (_, _) => onClick();
		return button;
	}

	private static Button Secondary(string text, Action onClick)
	{
		Button button = new()
		{
			Content = text,
			Padding = new Thickness(14, 6), CornerRadius = new CornerRadius(8),
			Background = ChatPalette.Panel, Foreground = ChatPalette.Body,
			BorderThickness = default,
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		button.Click += (_, _) => onClick();
		return button;
	}

	// ── 数据 ───────────────────────────────────────────────────────────────

	private bool IsInstalled(string modelId)
	{
		try
		{
			return SupportedModelIds.Normalize(modelId) is not null
				&& _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			return false;
		}
	}

	private string ProviderState(bool english)
	{
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		return chat.IsConfigured
			? english ? "Ready" : "已就绪"
			: english ? "Not set" : "未配置";
	}

	private string ProviderDetail()
	{
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		return chat.Model is {Length: > 0} model ? model : "—";
	}

	private int SkillCount()
	{
		try { return _services.Runtime?.Skills.GetEnabled().Count ?? 0; }
		catch { return 0; }
	}

	private int ToolCount()
	{
		try { return _services.Runtime?.Tools.ListEnabled().Count ?? 0; }
		catch { return 0; }
	}

	/// <summary>
	/// 模型 id 到缩略图资源名的映射。
	///
	/// 不能直接拿 id 拼文件名：id 是 <c>nori</c> / <c>arg-nori</c>，而 csproj 里
	/// 链进来的资源叫 <c>Nori.webp</c> / <c>ARGNori.webp</c>。第一版就是直接拼的，
	/// 结果每次都回退到 logo —— 不报错，只是头像一直是那朵花。
	/// </summary>
	private static string? ThumbnailFor(string modelId) => modelId switch
	{
		"nori" => "Assets/Models/Nori.webp",
		"arg-nori" => "Assets/Models/ARGNori.webp",
		_ => null,
	};

	/// <summary>形象缩略图。取不到就退回标志 —— 缺一张图不该让首页出不来。</summary>
	private static Control Avatar(string modelId)
	{
		foreach (string candidate in new[] {ThumbnailFor(modelId), "Assets/logo.png"}.OfType<string>())
		{
			try
			{
				return new Border
				{
					Width = 96, Height = 96,
					CornerRadius = new CornerRadius(48),
					ClipToBounds = true,
					Background = ChatPalette.Deep,
					Child = new Image
					{
						Stretch = Stretch.UniformToFill,
						Source = new Bitmap(AssetLoader.Open(new Uri($"avares://Nori.Desktop/{candidate}"))),
					},
				};
			}
			catch
			{
				// 换下一个候选。
			}
		}
		return new Border
		{
			Width = 96, Height = 96,
			CornerRadius = new CornerRadius(48),
			Background = ChatPalette.Deep,
		};
	}
}
