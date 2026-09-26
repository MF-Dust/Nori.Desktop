using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Main;

/// <summary>主界面一次刷新所需的、已在后台读完的模型与 AI 状态。</summary>
public readonly record struct HomeRefreshData(
	string ModelId,
	bool ModelReady,
	bool ChatConfigured,
	string ChatModel);

/// <summary>沿用 Vue 首页的角色舞台、运行概况、快捷入口与社区层级。</summary>
public sealed class HomeView : Panel
{
	private readonly AppServices _services;
	private readonly Action _onChanged;
	private readonly PluginWidgetHost _widgets;
	private readonly TextBlock _communityTitle = Text(12, ChatPalette.Muted, true);
	private readonly TextBlock _communityError = Text(12, ChatPalette.Danger);
	private readonly TextBlock _overviewTitle = Text(12, ChatPalette.Muted, true);
	private readonly TextBlock _shortcutsTitle = Text(12, ChatPalette.Muted, true);
	private readonly TextBlock _modelName = Text(22, ChatPalette.Primary, true);
	private readonly TextBlock _petStatus = Text(12, ChatPalette.Teal);
	private readonly TextBlock _petDescription = Text(13, ChatPalette.Muted);
	private readonly TextBlock _heroEyebrow = Text(12, ChatPalette.Muted, true);
	private readonly Image _avatar = new() {Stretch = Stretch.UniformToFill};
	private readonly Border _statusDot = new() {Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ChatPalette.Teal};
	private readonly Button _togglePet;
	private readonly Button _wave;
	private readonly Button _qq;
	private readonly Button _bilibili;
	private readonly Border _missingBanner;
	private readonly Border _safeBanner;
	private readonly TextBlock _missingTitle = Text(13, ChatPalette.Primary, true);
	private readonly TextBlock _missingDescription = Text(12, ChatPalette.Muted);
	private readonly TextBlock _safeTitle = Text(13, ChatPalette.Primary, true);
	private readonly TextBlock _safeDescription = Text(12, ChatPalette.Muted);
	private readonly Button _import;
	private readonly StatTile[] _stats = [new("cpu"), new("sparkles"), new("tool"), new("server")];
	private readonly ShortcutCard[] _shortcuts;
	private Bitmap? _avatarBitmap;
	private string? _avatarModelId;
	private int _qqCopyRevision;
	private bool _readingMcp;
	private bool _english;
	private bool _modelReady;
	private DateTimeOffset _qqCopiedUntil;

	/// <summary>最近一次首页刷新得到的形象安装状态，供底栏复用。</summary>
	internal bool ModelReady => _modelReady;

	public HomeView(AppServices services, Action onChanged)
	{
		_services = services;
		_onChanged = onChanged;
		_widgets = new PluginWidgetHost(services);
		_communityError.IsVisible = false;
		_togglePet = ActionButton("", TogglePet, true);
		_wave = ActionButton("", Wave);
		_import = ActionButton("", () => _services.Windows?.Show(WindowLabels.Models));
		_qq = ActionButton("QQ", () => _ = CopyQqAsync());
		_qq.Name = "CommunityQq";
		_qq.Margin = new Thickness(0, 0, 8, 8);
		_bilibili = CommunityLink("Bilibili", "https://space.bilibili.com/326505494");
		_missingBanner = Banner(_missingTitle, _missingDescription, _import);
		_safeBanner = Banner(_safeTitle, _safeDescription);
		_stats[3].Value.Name = "McpServersCount";
		_shortcuts =
		[
			new("chat", () => _services.Windows?.Show(WindowLabels.Chat)),
			new("models", () => _services.Windows?.Show(WindowLabels.Models)),
			new("memory", () => _services.Windows?.Show(WindowLabels.Memory)),
			new("settings", () => _services.Windows?.Show(WindowLabels.Settings)),
		];
		HomeLayoutPanel stats = new() {FourColumnMinimum = 700, TwoColumnMinimum = 280};
		foreach (StatTile tile in _stats) stats.Children.Add(tile.Root);
		HomeLayoutPanel shortcuts = new() {FourColumnMinimum = 700, TwoColumnMinimum = 360};
		foreach (ShortcutCard card in _shortcuts) shortcuts.Children.Add(card.Root);
		Children.Add(new StackPanel
		{
			Spacing = 16,
			Children =
			{
				new StackPanel {Spacing = 10, Children = {_missingBanner, _safeBanner, BuildHero()}},
				new StackPanel {Spacing = 8, Children = {_overviewTitle, stats}},
				new StackPanel {Spacing = 8, Children = {_shortcutsTitle, shortcuts}},
				_widgets,
				new Border
				{
					BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 18, 0, 0),
					Child = new StackPanel
					{
						Spacing = 12,
						Children =
						{
							_communityTitle,
							new WrapPanel
							{
								Children =
								{
									CommunityLink("Steam", "https://store.steampowered.com/app/4996280/I_NORI/"),
									CommunityLink("NoriOS", "https://os.inori.ai/landing"), _qq, _bilibili,
									CommunityLink("GitHub", "https://github.com/MF-Dust/Nori-Desktop-Pet"),
								},
							},
							_communityError,
						},
					},
				},
			},
		});
		AttachedToVisualTree += (_, _) => UpdateAvatar(_services.Config.GetStringOr(ConfigStore.KeySelectedModel, ConfigStore.DefaultModel));
		DetachedFromVisualTree += (_, _) => ReleaseAvatar();
	}

	/// <summary>仅更新现有控件的数据，保留键盘焦点、头像和展开的插件卡片。</summary>
	public void Refresh(bool english, HomeRefreshData data)
	{
		_english = english;
		_communityTitle.Text = english ? "COMMUNITY" : "生态社区";
		_overviewTitle.Text = english ? "AT A GLANCE" : "运行概况";
		_shortcutsTitle.Text = english ? "EXPLORE NORI" : "快速前往";
		_heroEyebrow.Text = english ? "YOUR DESKTOP COMPANION" : "你的桌面伙伴";
		_qq.Content = DateTimeOffset.UtcNow < _qqCopiedUntil ? english ? "Copied" : "已复制" : english ? "QQ group" : "QQ 交流群";
		_bilibili.Content = english ? "Bilibili" : "哔哩哔哩";
		_widgets.Refresh(english);
		if (!_readingMcp) _ = RefreshMcpCountAsync();
		string modelId = data.ModelId;
		_modelReady = data.ModelReady;
		bool petVisible = _services.Windows?.IsWindowVisible(WindowLabels.Pet) ?? false;
		_missingBanner.IsVisible = !_modelReady;
		_safeBanner.IsVisible = _services.SafeMode;
		_missingTitle.Text = english ? "Give Nori an appearance" : "为 Nori 准备一个形象";
		_missingDescription.Text = english ? "Install an appearance to welcome her to your desktop." : "安装模型后，就可以让她来到你的桌面。";
		_import.Content = english ? "Open Models" : "导入形象";
		_safeTitle.Text = english ? "Safe mode" : "安全模式";
		_safeDescription.Text = english ? "Tools, automation and plugins are off. Restart normally to use them." : "工具、自动化与插件已关闭，正常重启后即可使用。";
		_modelName.Text = modelId switch {"arg-nori" => "ARG Nori", "nori" => "Nori", _ => modelId};
		_petStatus.Text = petVisible ? english ? "On your desktop" : "正在陪伴" : english ? "Resting" : "休息中";
		_petStatus.Foreground = petVisible ? ChatPalette.Teal : ChatPalette.Muted;
		_statusDot.Background = petVisible ? ChatPalette.Teal : ChatPalette.Faint;
		_petDescription.Text = petVisible ? english ? "A little company for whatever your day brings." : "就在你的桌面，陪你度过每一个日常。"
			: english ? "Whenever you need a little company, she is here." : "想她的时候，轻轻召唤，让陪伴回到身边。";
		_togglePet.Content = !_modelReady ? english ? "Import an appearance" : "导入形象"
			: petVisible ? english ? "Hide Nori" : "收起 Nori" : english ? "Summon Nori" : "召唤 Nori";
		_wave.Content = english ? "Say hello" : "打个招呼";
		_wave.IsVisible = petVisible && _modelReady;
		UpdateAvatar(modelId);
		_stats[0].Set(english ? "Model provider" : "模型服务", data.ChatConfigured ? english ? "Ready" : "已就绪" : english ? "Not set" : "未配置",
			data.ChatModel is {Length: > 0} model ? model : english ? "Connect in Settings" : "前往设置连接模型", data.ChatConfigured);
		_stats[1].Set(english ? "Skills enabled" : "启用技能", SkillCount().ToString(), english ? "Ready to help" : "随时为你提供帮助", true);
		_stats[2].Set(english ? "Available tools" : "可用工具", ToolCount().ToString(), english ? "Built-in and MCP" : "内置能力与 MCP 工具", true);
		_stats[3].Set(english ? "MCP servers" : "MCP 服务", _stats[3].Value.Text ?? "—", english ? "Configured services" : "已配置的外部服务", false);
		_shortcuts[0].Set(english ? "Chat with Nori" : "和 Nori 聊聊", english ? "Ideas, stories, or just your day." : "分享灵感、心事，或今天的小事。", english ? "Start a conversation" : "开始对话");
		_shortcuts[1].Set(english ? "Appearance" : "模型换装", english ? "Find a look that feels like her." : "挑选喜欢的形象，发现新的表情。", english ? "Explore models" : "管理模型");
		_shortcuts[2].Set(english ? "Memories" : "共同的记忆", english ? "The little things she remembers." : "看看她记住的事，珍藏相处点滴。", english ? "View memories" : "查看记忆");
		_shortcuts[3].Set(english ? "Make it yours" : "偏好设置", english ? "Models, voice and abilities." : "调整模型服务、语音与各项能力。", english ? "Open Settings" : "打开设置");
	}

	private Control BuildHero()
	{
		Grid identity = new() {ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16};
		identity.Children.Add(new Border
		{
			Width = 68, Height = 68, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(34), ClipToBounds = true,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(2), Background = ChatPalette.Deep, Child = _avatar,
		});
		StackPanel details = new()
		{
			Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				_heroEyebrow,
				new WrapPanel
				{
					Children =
					{
						_modelName,
						new Border
						{
							Margin = new Thickness(12, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center,
							Background = ChatPalette.Overlay, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
							CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 4),
							Child = new StackPanel {Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = {_statusDot, _petStatus}},
						},
					},
				},
				_petDescription,
			},
		};
		Grid.SetColumn(details, 1);
		identity.Children.Add(details);
		_togglePet.Margin = new Thickness(0, 0, 10, 0);
		return new Border
		{
			CornerRadius = new CornerRadius(16), BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), Padding = new Thickness(16), ClipToBounds = true,
			Background = new LinearGradientBrush
			{
				StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
				GradientStops = {new GradientStop(((ISolidColorBrush)ChatPalette.Panel).Color, 0), new GradientStop(((ISolidColorBrush)ChatPalette.Deep).Color, 1)},
			},
			Child = new HomeHeroPanel {Children = {identity, new WrapPanel {VerticalAlignment = VerticalAlignment.Center, Children = {_togglePet, _wave}}}},
		};
	}

	private void TogglePet()
	{
		try
		{
			if (!_modelReady) _services.Windows?.Show(WindowLabels.Models);
			else if (_services.Windows?.IsWindowVisible(WindowLabels.Pet) == true) _services.Windows.Hide(WindowLabels.Pet);
			else _services.Windows?.Show(WindowLabels.Pet);
			_onChanged();
		}
		catch (Exception failure)
		{
			_petDescription.Text = _english ? "Unable to open Nori. Please try again." : "暂时无法打开伴侣，请重试。";
			_services.Logger.Write(LogSource.Backend, "warn", $"首页伴侣操作失败：{failure.GetType().Name}");
		}
	}

	private void Wave()
	{
		try { _services.PetRuntime?.PlayMotionByName("wave"); }
		catch (Exception failure)
		{
			_communityError.Text = _english ? "Could not play the greeting. Please try again." : "暂时无法播放招呼动作，请稍后重试。";
			_communityError.IsVisible = true;
			_services.Logger.Write(LogSource.Backend, "warn", $"播放动作失败：{failure.GetType().Name}");
		}
	}

	private static TextBlock Text(double size, IBrush brush, bool bold = false) => new()
	{
		FontSize = size, Foreground = brush, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap,
	};

	private static Border Banner(TextBlock title, TextBlock description, Button? action = null)
	{
		Grid content = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14};
		content.Children.Add(new StackPanel {Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = {title, description}});
		if (action is not null)
		{
			Grid.SetColumn(action, 1);
			action.VerticalAlignment = VerticalAlignment.Center;
			content.Children.Add(action);
		}
		return new Border {Background = ChatPalette.Overlay, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = content};
	}

	private static Button ActionButton(string text, Action onClick, bool primary = false)
	{
		Button button = new() {Content = text, Padding = new Thickness(15, 9), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13};
		if (primary) button.Classes.Add("primary");
		button.Click += (_, _) => onClick();
		return button;
	}

	private Button CommunityLink(string title, string url)
	{
		Button button = ActionButton(title, () => _ = OpenCommunityAsync(url));
		button.Tag = url;
		button.Margin = new Thickness(0, 0, 8, 8);
		return button;
	}

	private async Task OpenCommunityAsync(string url)
	{
		try { await Task.Run(() => ShellOpen.OpenUrl(url), _services.ShutdownToken); _communityError.IsVisible = false; }
		catch (Exception failure)
		{
			_communityError.Text = _english ? "Could not open the link. Please try again." : "打开链接失败，请稍后重试。";
			_communityError.IsVisible = true;
			_services.Logger.Write(LogSource.Backend, "warn", $"打开社区链接失败：{failure.GetType().Name}");
		}
	}

	private async Task CopyQqAsync()
	{
		try
		{
			IClipboard clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前窗口无法访问剪贴板");
			await clipboard.SetTextAsync("1041616195");
			int revision = ++_qqCopyRevision;
			_qqCopiedUntil = DateTimeOffset.UtcNow.AddSeconds(2);
			_qq.Content = _english ? "Copied" : "已复制";
			_communityError.IsVisible = false;
			_ = RevertQqLabelAsync(revision);
		}
		catch (Exception failure)
		{
			_communityError.Text = _english ? "Could not copy the QQ group number." : "复制 QQ 群号失败，请重试。";
			_communityError.IsVisible = true;
			_services.Logger.Write(LogSource.Backend, "warn", $"复制 QQ 群号失败：{failure.GetType().Name}");
		}
	}

	private async Task RevertQqLabelAsync(int revision)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(2), _services.ShutdownToken).ConfigureAwait(true);
		}
		catch (OperationCanceledException) when (_services.ShutdownToken.IsCancellationRequested)
		{
			return;
		}
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (revision != _qqCopyRevision || DateTimeOffset.UtcNow < _qqCopiedUntil) return;
			_qq.Content = _english ? "QQ group" : "QQ 交流群";
		});
	}

	private async Task RefreshMcpCountAsync()
	{
		_readingMcp = true;
		try
		{
			// SQLite 读取不占用 UI 线程；计数文本原位更新。
			int count = await Task.Run(() => _services.Mcp.GetServerConfigs().Count, _services.ShutdownToken);
			_stats[3].Value.Text = count.ToString();
		}
		catch (OperationCanceledException) when (_services.ShutdownToken.IsCancellationRequested) { }
		catch (Exception failure)
		{
			_stats[3].Value.Text = "—";
			_services.Logger.Write(LogSource.Backend, "warn", $"读取首页 MCP 统计失败：{failure.GetType().Name}");
		}
		finally { _readingMcp = false; }
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

	private static string? ThumbnailFor(string modelId) => modelId switch {"nori" => "Assets/Models/Nori.webp", "arg-nori" => "Assets/Models/ARGNori.webp", _ => null};

	private void UpdateAvatar(string modelId)
	{
		if (_avatarModelId == modelId) return;
		ReleaseAvatar();
		_avatarModelId = modelId;
		foreach (string candidate in new[] {ThumbnailFor(modelId), "Assets/logo.png"}.OfType<string>())
		{
			try
			{
				using Stream stream = AssetLoader.Open(new Uri($"avares://Nori.Desktop/{candidate}"));
				_avatarBitmap = new Bitmap(stream);
				_avatar.Source = _avatarBitmap;
				return;
			}
			catch (Exception failure) when (failure is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
			{
				// 缩略图不可读时继续尝试应用标志。
			}
		}
	}

	private void ReleaseAvatar()
	{
		_avatar.Source = null;
		_avatarBitmap?.Dispose();
		_avatarBitmap = null;
		_avatarModelId = null;
	}

	private sealed class StatTile
	{
		private readonly TextBlock _label = Text(12, ChatPalette.Muted);
		private readonly TextBlock _note = new() {FontSize = 12, Foreground = ChatPalette.Muted, TextTrimming = TextTrimming.CharacterEllipsis};
		internal TextBlock Value { get; } = Text(22, ChatPalette.Primary, true);
		internal Border Root { get; }
		internal StatTile(string icon)
		{
			Value.Text = "—";
			Root = new Border
			{
				Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
				Child = new StackPanel {Spacing = 8, Children = {new StackPanel {Orientation = Orientation.Horizontal, Spacing = 8, Children = {MainVisual.Icon(icon, 15, ChatPalette.Muted), _label}}, Value, _note}},
			};
		}
		internal void Set(string label, string value, string note, bool active)
		{
			_label.Text = label;
			Value.Text = value;
			Value.Foreground = active ? ChatPalette.Teal : ChatPalette.Primary;
			_note.Text = note;
			ToolTip.SetTip(Root, note);
		}
	}

	private sealed class ShortcutCard
	{
		private readonly TextBlock _title = Text(15, ChatPalette.Primary, true);
		private readonly TextBlock _description = Text(12, ChatPalette.Muted);
		private readonly TextBlock _action = Text(12, ChatPalette.Teal);
		internal Button Root { get; }
		internal ShortcutCard(string icon, Action open)
		{
			_description.MinHeight = 34;
			Grid bottom = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto")};
			bottom.Children.Add(_action);
			Control arrow = MainVisual.Icon("right", 15, ChatPalette.Teal);
			Grid.SetColumn(arrow, 1);
			bottom.Children.Add(arrow);
			Root = new Button
			{
				Name = "HomeShortcut", HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
				Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
				Content = new StackPanel
				{
					Spacing = 10,
					Children =
					{
						new Border
						{
							Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(12), Background = ChatPalette.Overlay,
							BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), Child = MainVisual.Icon(icon, 19, ChatPalette.Teal),
						},
						_title, _description,
						new Border {BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = bottom},
					},
				},
			};
			Root.Click += (_, _) => open();
		}
		internal void Set(string title, string description, string action)
		{
			_title.Text = title;
			_description.Text = description;
			_action.Text = action;
		}
	}
}
