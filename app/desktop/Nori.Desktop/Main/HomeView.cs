using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Ui;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Main;

/// <summary>
/// 主界面的首页。
///
/// 结构与 Vue 版 <c>components/home/HomePanel.vue</c> 对齐：告警段 → 角色舞台 →
/// 运行概况 → 快速前往。顺序不可调换 —— 需要用户处理的条件（形象缺失、安全模式）
/// 必须排在状态展示之前。
///
/// 文案取自 Vue 版的 i18n 词条（<c>views.main.home</c>），两端保持一致。原生这边
/// 多一张「记忆」卡：Vue 版没有对应窗口。
/// </summary>
public sealed class HomeView : Panel
{
	private readonly AppServices _services;
	private readonly Action _onChanged;
	private readonly StackPanel _body = new() {Spacing = 16};

	/// <summary>上一次画出来时的状态签名；相同就不重建，见 <see cref="Refresh"/>。</summary>
	private string _signature = "";

	public HomeView(AppServices services, Action onChanged)
	{
		_services = services;
		_onChanged = onChanged;
		Children.Add(_body);
	}

	/// <summary>页面当前的顶层控件实例。测试用来判断有没有发生重建。</summary>
	internal IReadOnlyList<Control> ChildrenForTests => [.. _body.Children.OfType<Control>()];

	/// <summary>
	/// 整幅重画。这一页很轻，重建比维护一堆通知源便宜。
	///
	/// **只在状态真的变了时重建**：主窗口每 2 秒调一次这里，无条件重建会把页面上所有
	/// 短时状态清掉 —— 指针停在卡片上时悬停态被重置，「已复制群号」这类操作反馈最多
	/// 活 2 秒、通常更短。签名覆盖这一页显示的每一个值。
	/// </summary>
	public void Refresh(bool english)
	{
		string modelId = _services.Config.GetStringOr(ConfigStore.KeySelectedModel, ConfigStore.DefaultModel);
		bool modelReady = IsInstalled(modelId);
		bool petVisible = _services.Windows?.IsWindowVisible(WindowLabels.Pet) ?? false;
		AiChatSettings chatSettings = _services.AiSettings.Read().Chat;
		int skillCount = SkillCount();
		int toolCount = ToolCount();
		int mcpCount = McpCount();

		string signature = string.Join('|',
			english ? "en" : "zh", modelId, modelReady, petVisible, _services.SafeMode,
			chatSettings.IsConfigured, chatSettings.Model, chatSettings.BaseUrl.Length > 0,
			skillCount, toolCount, mcpCount);
		if (signature == _signature && _body.Children.Count > 0) return;
		_signature = signature;

		_body.Children.Clear();

		// ── 告警段：需要用户处理的条件 ──────────────────────────────────
		if (!modelReady)
		{
			_body.Children.Add(Banner(
				english ? "Local Live2D model unavailable" : "本地 Live2D 模型不可用",
				english
					? "The model may have been moved, deleted, or damaged. Import ARG Nori or Nori again."
					: "模型可能被移动、删除或损坏，请重新导入 ARG Nori 或 Nori。",
				english ? "Import model" : "导入模型",
				() => _services.Windows?.Show(WindowLabels.Models)));
		}
		if (_services.SafeMode)
		{
			_body.Children.Add(Banner(
				english ? "Running in safe mode" : "安全模式运行中",
				english
					? "MCP auto-connect, proactive interaction and background maintenance are skipped. "
						+ "Restart to return to standard mode."
					: "已跳过 MCP 自动连接、主动交互与后台维护，重新启动即可回到标准模式。",
				null, null));
		}

		// ── 角色舞台 ────────────────────────────────────────────────────
		_body.Children.Add(Hero(english, modelId, modelReady, petVisible));

		// ── 运行概况 ────────────────────────────────────────────────────
		_body.Children.Add(SectionTitle(english ? "Runtime overview" : "运行概况"));
		_body.Children.Add(Cells(4,
		[
			Tile("cpu", english ? "Model provider" : "模型服务",
				chatSettings.IsConfigured
					? english ? "Ready" : "已就绪"
					: english ? "Not configured" : "未配置",
				ProviderDetail(chatSettings, english),
				chatSettings.IsConfigured ? Tone.Teal : Tone.Warning),
			Tile("sparkles", english ? "Enabled skills" : "启用技能", skillCount.ToString(),
				english ? "Toggle them in skill settings" : "可在技能设置中开关",
				skillCount > 0 ? Tone.Teal : Tone.Neutral),
			Tile("tool", english ? "Available tools" : "可用工具", toolCount.ToString(),
				english ? "Built-in and MCP tools" : "含内置工具与 MCP 工具",
				toolCount > 0 ? Tone.Teal : Tone.Neutral),
			Tile("server", english ? "MCP servers" : "MCP 服务", mcpCount.ToString(),
				english ? "Configured external tool servers" : "已配置的外部工具服务器",
				Tone.Neutral),
		]));

		// ── 快速前往 ────────────────────────────────────────────────────
		_body.Children.Add(SectionTitle(english ? "Quick access" : "快速前往"));
		_body.Children.Add(Cells(2,
		[
			Card("bot", english ? "AI companion" : "AI 对话伴侣",
				english ? "Chat with Nori about anything on your mind." : "随时随地与 Nori 畅聊各种话题与想法。",
				english ? "Start chat" : "开始对话",
				() => _services.Windows?.Show(WindowLabels.Chat)),
			Card("package", english ? "Model and outfits" : "模型换装",
				english ? "Switch outfits, costumes and manage expressions." : "切换不同造型外观与预设表情包。",
				english ? "Manage models" : "进入换装",
				() => _services.Windows?.Show(WindowLabels.Models)),
			Card("memory", english ? "Memory" : "记忆管理",
				english ? "Review and edit stored long-term memory entries." : "查看与编辑长期记忆条目。",
				english ? "Open memory" : "管理记忆",
				() => _services.Windows?.Show(WindowLabels.Memory)),
			Card("settings", english ? "Model provider settings" : "模型服务设置",
				english ? "Connect OpenAI, Claude, Gemini and more LLMs." : "支持 OpenAI、Claude、Gemini 等多种大模型接入。",
				english ? "Configure" : "连接配置",
				() => _services.Windows?.Show(WindowLabels.Settings)),
		]));

		// ── 生态社区 ────────────────────────────────────────────────────
		_body.Children.Add(new Border
		{
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0),
			Margin = new Thickness(0, 6, 0, 0),
		});
		_body.Children.Add(SectionTitle(english ? "Community and ecosystem" : "生态社区"));
		_body.Children.Add(Community(english));
	}

	// ── 各块 ───────────────────────────────────────────────────────────────

	/// <summary>
	/// 角色舞台。
	///
	/// 左侧是形象与状态，右侧是操作，两端分列 —— 与 Vue 版的 <c>justify-between</c>
	/// 一致。原生此前把操作按钮排在说明文字下面，整块因此挤在左半边，右侧留白与卡片
	/// 宽度不成比例。
	/// </summary>
	private Control Hero(bool english, string modelId, bool modelReady, bool petVisible)
	{
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 10,
			VerticalAlignment = VerticalAlignment.Center,
		};

		if (!modelReady)
		{
			actions.Children.Add(Primary(english ? "Import model" : "导入模型",
				() => _services.Windows?.Show(WindowLabels.Models)));
		}
		else if (petVisible)
		{
			actions.Children.Add(Secondary(english ? "Hide Nori" : "收起 Nori", () =>
			{
				_services.Windows?.Hide(WindowLabels.Pet);
				_onChanged();
			}));
			// 动作按钮只在形象已显示时给出：未显示时触发动作没有可见结果。
			actions.Children.Add(Secondary(english ? "Say hello" : "打个招呼", () =>
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
			actions.Children.Add(Primary(english ? "Bring Nori to desktop" : "唤出到桌面", () =>
			{
				_services.Windows?.Show(WindowLabels.Pet);
				_onChanged();
			}));
		}

		StackPanel identity = new()
		{
			Spacing = 6,
			VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				// 名称与状态标签同一行。Vue 版是 h2 + AppChip 并排，原生此前把状态
				// 降成名称下方的一行小字，状态因此读作副标题而不是状态。
				new StackPanel
				{
					Orientation = Orientation.Horizontal, Spacing = 10,
					Children =
					{
						new TextBlock
						{
							Text = modelId, FontSize = 19, FontWeight = FontWeight.SemiBold,
							Foreground = ChatPalette.Primary,
							VerticalAlignment = VerticalAlignment.Center,
						},
						Chip(petVisible
							? english ? "Companion active" : "伴侣已就绪"
							: english ? "Companion standby" : "待命休眠中", petVisible),
					},
				},
				new TextBlock
				{
					Text = petVisible
						? english ? "Nori is running on the desktop. Hide it here or from the tray."
							: "Nori 正在桌面上运行，可在此处或托盘收起。"
						: english ? "Nori is not running on the desktop."
							: "Nori 当前未在桌面上运行。",
					FontSize = 12, Foreground = ChatPalette.Muted,
					TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
				},
			},
		};

		StackPanel presence = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 18,
			VerticalAlignment = VerticalAlignment.Center,
			Children = {Avatar(modelId, petVisible), identity},
		};

		Grid row = new()
		{
			ColumnDefinitions = new ColumnDefinitions("*,Auto"),
			ColumnSpacing = 12,
			Margin = new Thickness(20, 18),
		};
		row.Children.Add(presence);
		row.Children.Add(actions);
		Grid.SetColumn(actions, 1);

		Panel stage = new()
		{
			Children =
			{
				// 左上角的径向光晕。位置与颜色取自 Vue 版同一处；这一层不接受命中测试，
				// 不影响下面按钮的点击。
				new Avalonia.Controls.Shapes.Ellipse
				{
					Width = 460, Height = 260,
					HorizontalAlignment = HorizontalAlignment.Left,
					VerticalAlignment = VerticalAlignment.Top,
					Margin = new Thickness(-70, -130, 0, 0),
					IsHitTestVisible = false,
					Opacity = 0.35,
					Fill = new RadialGradientBrush
					{
						GradientStops =
						[
							new GradientStop(Tint(ChatPalette.Teal, 0.55), 0),
							new GradientStop(Colors.Transparent, 0.68),
						],
					},
				},
				// 上沿的一条线：中间亮、两端透明。
				new Border
				{
					Height = 1,
					VerticalAlignment = VerticalAlignment.Top,
					IsHitTestVisible = false,
					Background = new LinearGradientBrush
					{
						StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
						EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
						GradientStops =
						[
							new GradientStop(Colors.Transparent, 0),
							new GradientStop(Tint(ChatPalette.Teal, 0.30), 0.5),
							new GradientStop(Colors.Transparent, 1),
						],
					},
				},
				row,
			},
		};

		return new Border
		{
			Background = ChatPalette.Panel,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(14),
			ClipToBounds = true,   // 光晕要被圆角裁住，否则会溢出到卡片之外
			Child = stage,
		};
	}

	/// <summary>
	/// 生态社区。
	///
	/// 与 Vue 版同一组入口、同一份品牌标记路径。QQ 那一项不是链接：客户端无法打开
	/// QQ 的加群协议，所以复制群号到剪贴板，并把按钮文字换成结果 —— 复制没有其它
	/// 可见反馈，不换文字用户无法确认是否执行。
	/// </summary>
	private Control Community(bool english)
	{
		WrapPanel row = new() {Margin = new Thickness(0, 0, -8, -8)};

		foreach ((string icon, bool brand, string label, string url) in new[]
		{
			("steam", true, english ? "Steam store" : "Steam 页面", "https://store.steampowered.com/app/4996280/I_NORI/"),
			("noriOS", false, english ? "NoriOS official" : "NoriOS 官网", "https://os.inori.ai/landing"),
			("bilibili", true, english ? "Bilibili" : "Bilibili 主页", "https://space.bilibili.com/326505494"),
			("github", true, english ? "GitHub (community)" : "GitHub（社区开源）", "https://github.com/MF-Dust/Nori-Desktop-Pet"),
		})
		{
			row.Children.Add(LinkButton(icon, brand, label, () =>
			{
				try { Runtime.ShellOpen.OpenUrl(url); }
				catch (Exception failure)
				{
					_services.Logger.Write(LogSource.Backend, "warn", $"打开链接失败：{failure.Message}");
				}
			}));
		}

		TextBlock qqLabel = new()
		{
			Text = english ? "QQ group" : "QQ 交流群", FontSize = 11,
			Foreground = ChatPalette.Body,
			VerticalAlignment = VerticalAlignment.Center,
		};
		row.Children.Add(LinkButton("qq", true, qqLabel, async button =>
		{
			try
			{
				IClipboard? clipboard = TopLevel.GetTopLevel(button)?.Clipboard;
				if (clipboard is null) return;
				await clipboard.SetTextAsync(QqGroup);
				qqLabel.Text = english ? "Group number copied" : "已复制群号";
				qqLabel.Foreground = Online;
				DispatcherTimer.RunOnce(() =>
				{
					qqLabel.Text = english ? "QQ group" : "QQ 交流群";
					qqLabel.Foreground = ChatPalette.Body;
				}, TimeSpan.FromSeconds(2));
			}
			catch (Exception failure)
			{
				_services.Logger.Write(LogSource.Backend, "warn", $"复制群号失败：{failure.Message}");
			}
		}));

		return row;
	}

	/// <summary>QQ 交流群群号。与 Vue 版同一个值。</summary>
	private const string QqGroup = "1041616195";

	private static Button LinkButton(string icon, bool brand, string label, Action onClick) =>
		LinkButton(icon, brand, new TextBlock
		{
			Text = label, FontSize = 11, Foreground = ChatPalette.Body,
			VerticalAlignment = VerticalAlignment.Center,
		}, button =>
		{
			onClick();
			return Task.CompletedTask;
		});

	private static Button LinkButton(string icon, bool brand, TextBlock label, Func<Button, Task> onClick)
	{
		Button button = new()
		{
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(12, 5),
			Margin = new Thickness(0, 0, 8, 8),
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		button.Content = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 7,
			Children =
			{
				brand ? LineIcon.Filled(icon, ChatPalette.Muted, 13) : LineIcon.Build(icon, ChatPalette.Muted, 13),
				label,
			},
		};
		button.Click += async (_, _) => await onClick(button);
		return button;
	}

	/// <summary>状态标签：一个圆点加一行字，与 Vue 版 AppChip 的 dot 形态对应。</summary>
	private static Control Chip(string text, bool on) => new Border
	{
		Background = Tinted(on ? Online : ChatPalette.Faint, 0.14),
		BorderBrush = Tinted(on ? Online : ChatPalette.Faint, 0.38),
		BorderThickness = new Thickness(1),
		CornerRadius = new CornerRadius(999),
		Padding = new Thickness(9, 3),
		VerticalAlignment = VerticalAlignment.Center,
		Child = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 6,
			Children =
			{
				new Avalonia.Controls.Shapes.Ellipse
				{
					Width = 6, Height = 6,
					Fill = on ? Online : ChatPalette.Faint,
					VerticalAlignment = VerticalAlignment.Center,
				},
				new TextBlock
				{
					Text = text, FontSize = 11,
					Foreground = on ? Online : ChatPalette.Muted,
					VerticalAlignment = VerticalAlignment.Center,
				},
			},
		},
	};

	private static Control SectionTitle(string text) => new TextBlock
	{
		Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold,
		Foreground = ChatPalette.Faint,
		// Vue 版这一行带 0.06rem 字距，用于把分组标题与下面的内容拉开层级。
		LetterSpacing = 0.6,
		Margin = new Thickness(2, 6, 0, 0),
	};

	/// <summary>
	/// 等宽分栏。
	///
	/// Vue 版用 <c>grid-cols-4</c> / <c>grid-cols-3</c>，各列等宽并填满整行。原生
	/// 此前用 WrapPanel 加固定宽度，窗口宽度不是列宽整数倍时末行留下一段空白
	/// （实测 960 宽下四块概况排成 3 + 1）。
	/// </summary>
	private static Control Cells(int columns, IReadOnlyList<Control> items)
	{
		Grid grid = new() {ColumnSpacing = 10, RowSpacing = 10};
		for (int index = 0; index < columns; index++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		for (int index = 0; index < (items.Count + columns - 1) / columns; index++)
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		for (int index = 0; index < items.Count; index++)
		{
			Grid.SetColumn(items[index], index % columns);
			Grid.SetRow(items[index], index / columns);
			grid.Children.Add(items[index]);
		}
		return grid;
	}

	/// <summary>概况数值的色调。与 Vue 版 AppStatTile 的 tone 对应。</summary>
	private enum Tone
	{
		/// <summary>正常，用强调色。</summary>
		Teal,

		/// <summary>缺配置，用告警色。</summary>
		Warning,

		/// <summary>计数为 0 或无倾向，用正文色。</summary>
		Neutral,
	}

	private static Control Tile(string icon, string label, string value, string note, Tone tone)
	{
		IBrush accent = tone switch
		{
			Tone.Teal => ChatPalette.Teal,
			Tone.Warning => Warning,
			_ => ChatPalette.Primary,
		};

		return new Border
		{
			Background = ChatPalette.Panel,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(12),
			Padding = new Thickness(14, 12),
			Child = new StackPanel
			{
				Spacing = 5,
				Children =
				{
					// 图标与标签同一行：Vue 版 AppStatTile 也是这个排法，图标承担分类，
					// 标签承担名称，两者分行会让每块多占一行而信息量不变。
					new StackPanel
					{
						Orientation = Orientation.Horizontal, Spacing = 6,
						Children =
						{
							LineIcon.Build(icon, ChatPalette.Faint, 14),
							new TextBlock
							{
								Text = label, FontSize = 11, Foreground = ChatPalette.Faint,
								TextTrimming = TextTrimming.CharacterEllipsis,
								VerticalAlignment = VerticalAlignment.Center,
							},
						},
					},
					// 数值与说明都不换行：数值可能是模型名这类长串，换行会让同一行的
					// 四块高度不一致。
					new TextBlock
					{
						Text = value, FontSize = 19, FontWeight = FontWeight.SemiBold,
						Foreground = accent, TextTrimming = TextTrimming.CharacterEllipsis,
					},
					new TextBlock
					{
						Text = note, FontSize = 11, Foreground = ChatPalette.Faint,
						TextTrimming = TextTrimming.CharacterEllipsis,
					},
				},
			},
		};
	}

	/// <summary>
	/// 快速前往的入口卡。
	///
	/// 结构与 Vue 版一致：图标框 + 标题一行，说明一段，底部用一条分隔线隔出动作行
	/// （动作名 + 右箭头）。底部这一行是「这张卡可点」的唯一明示 —— 只有标题和说明
	/// 时，它与旁边不可点的概况块在外观上没有区别。
	///
	/// 动作行固定在卡片底部（Vue 版的 <c>mt-auto</c>），同一行内说明长度不同的卡片
	/// 因此仍然对齐。
	/// </summary>
	private static Control Card(string icon, string title, string description, string action, Action onOpen)
	{
		TextBlock heading = new()
		{
			Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold,
			Foreground = ChatPalette.Primary,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
		};

		// 图标框：描边 + 低透明度的强调色底，与 Vue 版的 32px 方框对应。
		Border iconBox = new()
		{
			Width = 32, Height = 32,
			CornerRadius = new CornerRadius(8),
			Background = Tinted(ChatPalette.Teal, 0.10),
			BorderBrush = Tinted(ChatPalette.Teal, 0.28), BorderThickness = new Thickness(1),
			Child = LineIcon.Build(icon, ChatPalette.Teal, 17),
		};

		TextBlock actionText = new()
		{
			Text = action, FontSize = 11, FontWeight = FontWeight.Medium,
			Foreground = ChatPalette.Muted,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Control arrow = LineIcon.Build("arrow-right", ChatPalette.Muted, 13);
		arrow.HorizontalAlignment = HorizontalAlignment.Right;

		Grid actionRow = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto")};
		actionRow.Children.Add(actionText);
		actionRow.Children.Add(arrow);
		Grid.SetColumn(arrow, 1);

		Border footer = new()
		{
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0),
			Margin = new Thickness(0, 10, 0, 0),
			Padding = new Thickness(0, 8, 0, 0),
			Child = actionRow,
		};

		DockPanel content = new() {LastChildFill = true};
		DockPanel.SetDock(footer, Dock.Bottom);
		content.Children.Add(footer);
		content.Children.Add(new StackPanel
		{
			Spacing = 8,
			Children =
			{
				new StackPanel
				{
					Orientation = Orientation.Horizontal, Spacing = 10,
					Children = {iconBox, heading},
				},
				new TextBlock
				{
					Text = description, FontSize = 11, Foreground = ChatPalette.Muted,
					TextWrapping = TextWrapping.Wrap, LineHeight = 17,
				},
			},
		});

		Border card = new()
		{
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(12),
			Padding = new Thickness(16, 14),
			Cursor = new Cursor(StandardCursorType.Hand),
			Child = content,
		};

		// 悬停反馈。Vue 版另加了上浮位移，原生这里只改描边与底色 —— 位移会让同一行
		// 其余卡片在指针移动时反复重排。
		card.PointerEntered += (_, _) =>
		{
			card.BorderBrush = Tinted(ChatPalette.Teal, 0.45);
			card.Background = ChatPalette.Panel;
			heading.Foreground = ChatPalette.Teal;
			actionText.Foreground = ChatPalette.Teal;
		};
		card.PointerExited += (_, _) =>
		{
			card.BorderBrush = ChatPalette.Line;
			card.Background = ChatPalette.Deep;
			heading.Foreground = ChatPalette.Primary;
			actionText.Foreground = ChatPalette.Muted;
		};
		// 只认左键：不判按键的话右键和中键也会开窗，而右键在这一带没有任何菜单，
		// 用户按下去只会莫名其妙多开一扇窗。
		card.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(card).Properties.IsLeftButtonPressed) onOpen();
		};
		return card;
	}

	/// <summary>
	/// 告警段。
	///
	/// 与 Vue 版一致：整框描边加低透明度底色，左侧一个告警图标，操作按钮靠右。
	/// 原生此前只画底边一条 2px 的线、底色与页面背景相同，整段因此读作一段带下划线
	/// 的正文而不是一条告警。
	/// </summary>
	private static Control Banner(string title, string description, string? action, Action? onAction)
	{
		StackPanel text = new()
		{
			Spacing = 2,
			VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				new TextBlock
				{
					Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold,
					Foreground = ChatPalette.Primary,
				},
				new TextBlock
				{
					Text = description, FontSize = 11, Foreground = ChatPalette.Muted,
					TextWrapping = TextWrapping.Wrap,
				},
			},
		};

		StackPanel left = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 10,
			VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				LineIcon.Build("alert", Warning, 17),
				text,
			},
		};

		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12};
		row.Children.Add(left);
		if (action is not null && onAction is not null)
		{
			Button button = Secondary(action, onAction);
			button.VerticalAlignment = VerticalAlignment.Center;
			row.Children.Add(button);
			Grid.SetColumn(button, 1);
		}

		return new Border
		{
			Background = Tinted(Warning, 0.08),
			BorderBrush = Tinted(Warning, 0.35), BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(10),
			Padding = new Thickness(16, 12),
			Child = row,
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

	/// <summary>模型服务那一块的第二行：模型名优先，其次服务商，都没有就说明未选择。</summary>
	private static string ProviderDetail(AiChatSettings chat, bool english)
	{
		if (chat.Model is {Length: > 0} model) return model;
		if (chat.BaseUrl.Length > 0) return chat.Provider.ToString();
		return english ? "No provider selected yet" : "尚未选择服务商";
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

	private int McpCount()
	{
		try { return _services.Mcp.GetServerConfigs().Count; }
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

	/// <summary>
	/// 解码过的缩略图，按资源路径缓存。
	///
	/// 解码结果只跟资源路径有关，而这一页会被反复重画（切换形象、召唤与收起都要重画）。
	/// 不缓存的话每次重画都要解一遍 webp，而且解出来的 <see cref="Bitmap"/> 没有释放点。
	/// 读不出来时记 null，避免每次重画都再试一遍一个不存在的资源。
	/// </summary>
	private static readonly Dictionary<string, Bitmap?> Thumbnails = [];

	private static Bitmap? Thumbnail(string candidate)
	{
		if (Thumbnails.TryGetValue(candidate, out Bitmap? cached)) return cached;
		Bitmap? bitmap = null;
		try
		{
			bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://Nori.Desktop/{candidate}")));
		}
		catch (Exception failure) when (failure is IOException or UriFormatException
			or ArgumentException or InvalidOperationException)
		{
			// 缺一张图不该让首页出不来，退回下一个候选。
		}
		Thumbnails[candidate] = bitmap;
		return bitmap;
	}

	/// <summary>
	/// 形象缩略图。
	///
	/// 圆形描边环加右下角状态点，与 Vue 版一致：状态点让「是否在桌面上运行」在头像上
	/// 就能读到，不必移到旁边的文字。缩略图对齐上沿（Vue 版的 <c>object-top</c>）——
	/// 立绘是全身像，居中裁切会把头部裁到圆环之外。取不到图就退回标志。
	/// </summary>
	private static Control Avatar(string modelId, bool petVisible)
	{
		Control inner = new Border {Background = ChatPalette.Deep};
		foreach (string candidate in new[] {ThumbnailFor(modelId), "Assets/logo.png"}.OfType<string>())
		{
			if (Thumbnail(candidate) is not {} bitmap) continue;
			inner = new Image
			{
				Stretch = Stretch.UniformToFill,
				VerticalAlignment = VerticalAlignment.Top,
				Source = bitmap,
			};
			break;
		}

		Border ring = new()
		{
			Width = 92, Height = 92,
			CornerRadius = new CornerRadius(46),
			ClipToBounds = true,
			Background = ChatPalette.Deep,
			BorderBrush = Tinted(ChatPalette.Teal, petVisible ? 0.45 : 0.20),
			BorderThickness = new Thickness(2),
			Child = inner,
		};

		// 状态点压在头像右下角，用底色描边与头像分开。
		Border dot = new()
		{
			Width = 17, Height = 17,
			CornerRadius = new CornerRadius(9),
			Background = petVisible ? Online : ChatPalette.Faint,
			BorderBrush = ChatPalette.Background, BorderThickness = new Thickness(2.5),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
		};

		return new Panel
		{
			Width = 96, Height = 96,
			VerticalAlignment = VerticalAlignment.Center,
			Children = {ring, dot},
		};
	}

	// ── 配色 ───────────────────────────────────────────────────────────────

	/// <summary>在线状态的绿。ChatPalette 里没有，与 Vue 版的 --success 取同一个值。</summary>
	private static readonly IBrush Online = new ImmutableSolidColorBrush(Color.Parse("#20e090"));

	/// <summary>告警色，与 Vue 版的 --warning 对应。</summary>
	private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.Parse("#e8b168"));

	private static Color Tint(IBrush brush, double alpha)
	{
		Color color = brush is ISolidColorBrush solid ? solid.Color : Colors.White;
		return Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
	}

	private static IBrush Tinted(IBrush brush, double alpha) => new ImmutableSolidColorBrush(Tint(brush, alpha));
}
