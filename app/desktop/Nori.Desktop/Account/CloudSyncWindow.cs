using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Cloud;
using Nori.Core.Platform;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Account;

/// <summary>
/// 云端同步。
///
/// ── 三个操作的可逆性不同，视觉层级依此划分 ────────────────────────────────
///   备份   覆盖云端存档，由版本检查拦截并发冲突。
///   恢复   合并至本机（配置覆盖，记忆与提醒只增不删），无数据丢失风险。
///   删除   不可逆，是唯一会移除数据的操作。单独置于末尾，使用告警色，需两次确认。
///
/// ── 冲突处置不另开窗口 ────────────────────────────────────────────────────
/// 云端版本较新时，在原位追加「以本机为准覆盖」按钮。另开模态窗会使用户需要在两个
/// 窗口之间比对云端存档的时间，而该信息位于本窗口。
/// </summary>
internal sealed class CloudSyncWindow : Window
{
	private readonly CloudSyncService _sync;
	private readonly CancellationToken _cancel;

	private readonly Ui.SyncTether _tether = new();
	private readonly TextBlock _here = Cap("本机", ChatPalette.Accent, HorizontalAlignment.Left);
	private readonly TextBlock _there = Cap("云端", ChatPalette.Teal, HorizontalAlignment.Right);
	private readonly TextBlock _localLine = new();
	private readonly TextBlock _remote = new();
	private readonly TextBlock _result = new();
	private readonly Button _backup;
	private readonly Button _restore;
	private readonly Button _overwrite;
	private readonly Button _forget;

	/// <summary>删除需两次点击。第一次切换为确认文案，第二次执行。</summary>
	private bool _forgetArmed;

	/*
	 * 按钮可用性由三个条件组合决定：是否已登录、云端是否有存档、是否有操作进行中。
	 * 三者各自保存，统一由 ApplyEnabled 计算 —— 原 SetBusy(false) 无条件启用全部
	 * 按钮，导致每次刷新后未登录状态下的备份/恢复/删除重新变为可用。
	 */
	private bool _signedIn;
	private bool _hasRemote;
	private bool _busy;

	internal CloudSyncWindow(CloudSyncService sync, CancellationToken cancel = default)
	{
		_sync = sync;
		_cancel = cancel;

		Title = "云端同步";
		Width = 460;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;

		_backup = Action("备份到云端", ChatPalette.Teal, ChatPalette.OnTeal, () => Run(Backup));
		_restore = Action("从云端恢复", Brushes.Transparent, ChatPalette.Body, () => Run(Restore));
		_overwrite = Action("以本机为准覆盖", Brushes.Transparent, ChatPalette.Accent, () => Run(Overwrite));
		_forget = Action("删除云端存档", Brushes.Transparent, ChatPalette.Danger, ArmOrForget);
		// 不占满整行：整行宽度的告警色按钮在视觉层级上等同主操作，而删除是不可逆操作。
		_forget.HorizontalAlignment = HorizontalAlignment.Left;
		_overwrite.IsVisible = false;

		Content = BuildBody();

		KeyDown += (_, args) =>
		{
			if (args.Key != Key.Escape) return;
			args.Handled = true;
			Close();
		};
		Opened += (_, _) => Run(Refresh);
		// 定时器不会自行停止。窗口关闭后若未停止，它会持续重绘已销毁的控件。
		Closed += (_, _) => _tether.Stop();
	}

	private Control BuildBody()
	{
		StackPanel body = new() {Spacing = 0};
		body.Children.Add(BuildChrome());

		StackPanel inner = new() {Spacing = 14, Margin = new Thickness(24, 20, 24, 20)};

		inner.Children.Add(new TextBlock
		{
			Text = "同步偏好、记忆与提醒。不含对话原文。",
			FontSize = 13, LineHeight = 21,
			TextWrapping = TextWrapping.Wrap,
			Foreground = ChatPalette.Body,
		});

		inner.Children.Add(BuildLink());

		inner.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10,
			Children = {_backup, _restore, _overwrite},
		});

		_result.FontSize = 12;
		_result.LineHeight = 20;
		_result.TextWrapping = TextWrapping.Wrap;
		_result.Foreground = ChatPalette.Faint;
		_result.IsVisible = false;
		inner.Children.Add(_result);

		inner.Children.Add(new Border
		{
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(0, 1, 0, 0),
			Margin = new Thickness(0, 4, 0, 0),
			Padding = new Thickness(0, 12, 0, 0),
			Child = new StackPanel
			{
				Spacing = 8,
				Children =
				{
					new TextBlock
					{
						Text = "删除只影响云端那一份，本机数据不受影响。",
						FontSize = 12,
						Foreground = ChatPalette.Faint,
						TextWrapping = TextWrapping.Wrap,
					},
					_forget,
				},
			},
		});

		// 本窗口全部操作均针对存放在 NCN 上的数据，归属标识置于此处。
		Control badge = PoweredByNcn.Build();
		badge.Margin = new Thickness(0, 0, 0, 16);
		inner.Children.Add(badge);

		body.Children.Add(inner);
		return body;
	}

	/// <summary>
	/// 两个端点及其连线。
	///
	/// 端点标题使用各自的颜色（本机 = accent，云端 = teal），连线渐变延续该对应关系；
	/// 下方两行明细分别左右对齐到对应端。存在性与一致性因此无需阅读文字即可判断。
	/// </summary>
	private Control BuildLink()
	{
		Grid grid = new()
		{
			ColumnDefinitions = new ColumnDefinitions("*,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
		};

		Grid.SetColumnSpan(_tether, 2);
		grid.Children.Add(_tether);

		grid.Children.Add(_here);
		grid.Children.Add(_there);
		Grid.SetRow(_here, 1);
		Grid.SetRow(_there, 1);
		Grid.SetColumn(_there, 1);

		Detail(_localLine, HorizontalAlignment.Left);
		Detail(_remote, HorizontalAlignment.Right);
		_remote.Text = "正在读取…";
		grid.Children.Add(_localLine);
		grid.Children.Add(_remote);
		Grid.SetRow(_localLine, 2);
		Grid.SetRow(_remote, 2);
		Grid.SetColumn(_remote, 1);

		return grid;
	}

	private static TextBlock Cap(string text, IBrush color, HorizontalAlignment align) => new()
	{
		Text = text,
		FontSize = 12,
		FontWeight = FontWeight.Medium,
		Foreground = color,
		HorizontalAlignment = align,
		Margin = new Thickness(0, 4, 0, 0),
	};

	private static void Detail(TextBlock block, HorizontalAlignment align)
	{
		block.FontSize = 11.5;
		block.LineHeight = 18;
		block.Foreground = ChatPalette.Faint;
		block.HorizontalAlignment = align;
		block.TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
		block.TextWrapping = TextWrapping.Wrap;
		// 版本号与体积需按列对齐比较，须使用等宽数字。
		block.FontFeatures = FontFeatureCollection.Parse("tnum");
	}

	private Control BuildChrome()
	{
		TextBlock title = new()
		{
			Text = "云端同步",
			FontSize = 12, FontWeight = FontWeight.SemiBold,
			Foreground = ChatPalette.Accent,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Button close = new()
		{
			Content = "✕",
			Width = 40, Height = 40,
			Background = Brushes.Transparent,
			BorderThickness = default,
			Foreground = ChatPalette.Muted,
			FontSize = 12,
			HorizontalAlignment = HorizontalAlignment.Right,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		close.Click += (_, _) => Close();

		Border bar = new()
		{
			Height = 40,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(20, 0, 0, 0),
			Child = new Grid {Children = {title, close}},
		};
		bar.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
		};
		return bar;
	}

	private Button Action(string label, IBrush background, IBrush foreground, Action onClick)
	{
		Button button = new()
		{
			Content = label,
			Padding = new Thickness(14, 8),
			Background = background,
			Foreground = foreground,
			BorderBrush = ReferenceEquals(background, Brushes.Transparent) ? ChatPalette.Panel : background,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(6),
			FontSize = 13,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		button.Click += (_, _) => onClick();
		return button;
	}

	/// <summary>删除需两次点击。第一次仅切换文案，不发起请求。</summary>
	private void ArmOrForget()
	{
		if (!_forgetArmed)
		{
			_forgetArmed = true;
			// 确认态文案需说明按下后执行的操作，不使用「确定」。
			_forget.Content = "确认删除云端存档";
			_forget.BorderBrush = ChatPalette.Danger;
			return;
		}
		Run(Forget);
	}

	private void DisarmForget()
	{
		_forgetArmed = false;
		_forget.Content = "删除云端存档";
		_forget.BorderBrush = ChatPalette.Panel;
	}

	/// <summary>包装一次异步操作：禁用按钮、执行、恢复按钮、刷新状态。</summary>
	private async void Run(Func<Task> work)
	{
		SetBusy(true);
		try
		{
			await work();
		}
		catch (Exception error) when (error is not OutOfMemoryException)
		{
			// 未预期的异常须呈现在界面上。静默失败会被理解为操作成功。
			Say("操作失败：" + error.GetType().Name, ChatPalette.Danger);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async Task Refresh()
	{
		CloudSyncStatus status = await _sync.StatusAsync(_cancel);
		_signedIn = status.SignedIn;
		// 读不到云端状态时按「没有」处理：备份仍然可以做（它不依赖云端那份），
		// 恢复与删除不行 —— 对一份看不见的东西执行它们没有意义。
		_hasRemote = status.SignedIn && status.Ok && status.Present;

		/*
		 * 两端分别呈现各自的状态。
		 *
		 * 原实现仅用一段文字描述云端存档，不显示本机侧信息 —— 而本机已同步到的版本号
		 * 是判断是否需要备份的唯一依据。现两端对称呈现，版本差异可直接比较。
		 */
		_localLine.Text = !status.SignedIn
			? "未登录"
			: status.LocalKnownRevision > 0 ? $"第 {status.LocalKnownRevision} 版" : "尚未同步";

		_remote.Text = !status.SignedIn
			? "需要账户"
			: !status.Ok
				? status.Error
				: status.Present
					? $"第 {status.Revision} 版\n{Readable(status.SavedAt)}\n{status.Bytes / 1024} KB · {Blank(status.AppVersion)}"
					: "还没有存档";

		_tether.State = !status.SignedIn ? Ui.TetherState.SignedOut
			: !status.Ok || !status.Present ? Ui.TetherState.NoRemote
			: status.Revision == status.LocalKnownRevision ? Ui.TetherState.InStep
			: Ui.TetherState.OutOfStep;

		// 端点标题随连线一同降低不透明度：未登录时两端所指的存档均不存在，
		// 标题的对比度不应高于它所标注的图形。
		double caps = status.SignedIn ? 1 : 0.45;
		_here.Opacity = caps;
		_there.Opacity = caps;

		ApplyEnabled();
	}

	private async Task Backup()
	{
		CloudSyncResult result = await _sync.BackupAsync(overwrite: false, _cancel);
		Report(result);
		// 仅在冲突时显示覆盖操作。无冲突时提供「无条件覆盖」会被当作普通备份使用。
		_overwrite.IsVisible = result.Conflict;
		await Settle(result, Ui.TetherDirection.Up);
	}

	private async Task Overwrite()
	{
		CloudSyncResult result = await _sync.BackupAsync(overwrite: true, _cancel);
		Report(result);
		_overwrite.IsVisible = false;
		await Settle(result, Ui.TetherDirection.Up);
	}

	private async Task Restore()
	{
		CloudSyncResult result = await _sync.RestoreAsync(_cancel);
		Report(result);
		_overwrite.IsVisible = false;
		await Settle(result, Ui.TetherDirection.Down);
	}

	/// <summary>
	/// 收尾：先刷新两端状态，成功时沿连线播放一段方向高亮。
	///
	/// 顺序固定：先刷新，使高亮播放结束时连线已处于新状态（多数情况下为实线），
	/// 传输与状态变化因此呈现为一次连续过程。
	///
	/// 失败时不播放：没有发生传输。
	/// </summary>
	private async Task Settle(CloudSyncResult result, Ui.TetherDirection direction)
	{
		await Refresh();
		if (result.Ok) _tether.Travel(direction);
	}

	private async Task Forget()
	{
		CloudSyncResult result = await _sync.ForgetAsync(_cancel);
		DisarmForget();
		Report(result);
		await Refresh();
	}

	/// <summary>
	/// 显示操作结果，并列出被跳过的内容。
	///
	/// 部分内容未传输且无提示，是此类功能最难发现的缺陷：备份显示为成功，
	/// 在另一台设备恢复时才发现数据不完整。
	/// </summary>
	private void Report(CloudSyncResult result)
	{
		string text = result.Message;
		if (result.Skipped.Count > 0)
		{
			text += "\n未包含：" + string.Join("；", result.Skipped);
		}
		Say(text, result.Ok ? ChatPalette.Teal : ChatPalette.Danger);
	}

	private void Say(string text, IBrush tone)
	{
		_result.Text = text;
		_result.Foreground = tone;
		_result.IsVisible = text.Length > 0;
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		if (busy) DisarmForget();
		ApplyEnabled();
	}

	/// <summary>
	/// 按当前状态重算四个按钮的可用性。
	///
	/// IsEnabled 只在此处修改。分散在 Refresh 与 SetBusy 中各写一部分时，二者的执行
	/// 顺序会成为行为的一部分 —— 这正是「未登录时按钮全部可用」的成因。
	/// </summary>
	private void ApplyEnabled()
	{
		Gate(_backup, _signedIn && !_busy);
		Gate(_overwrite, _signedIn && !_busy);
		// 恢复与删除都针对云端那一份；没有那一份时它们无事可做。
		Gate(_restore, _signedIn && _hasRemote && !_busy);
		Gate(_forget, _signedIn && _hasRemote && !_busy);
	}

	/// <summary>
	/// 禁用按钮，并同步呈现禁用态。
	///
	/// 这些按钮显式设置了 Background 与 Foreground，覆盖了主题为禁用态提供的画刷。
	/// 仅设置 IsEnabled 时按钮外观不变，点击无响应且无提示。降低不透明度是此处
	/// 表示不可用的唯一手段。
	/// </summary>
	private static void Gate(Button button, bool enabled)
	{
		button.IsEnabled = enabled;
		button.Opacity = enabled ? 1 : 0.38;
	}

	/// <summary>视觉测试用：播放一次高亮段，不执行同步。</summary>
	internal void TravelForTests(Ui.TetherDirection direction) => _tether.Travel(direction);

	/// <summary>将 ISO 时刻转换为本地时间显示。原始格式不适合直接阅读。</summary>
	internal static string Readable(string iso)
	{
		if (DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset when))
		{
			return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
		}
		return iso.Length > 0 ? iso : "时间未知";
	}

	private static string Blank(string value) => value.Length > 0 ? value : "未知";
}
