using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Windows;

/// <summary>独立且仅深色的原生对话窗口；普通关闭只隐藏，保留当前会话与未发送草稿。</summary>
public sealed class ChatWindow : Window
{
	private readonly NativeChatService _service;
	private bool _closing;
	private bool _prepared;

	/// <summary>建立原生对话宿主和可复用正文，不创建 WebView。</summary>
	public ChatWindow(AppServices services)
	{
		Width = 960; Height = 640; MinWidth = 720; MinHeight = 480;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml") });
		Background = ChatPalette.Background;
		_service = new NativeChatService(services, this);
		Body = new ChatView(_service) { Margin = new Thickness(12) }; Content = Body;
		UpdateTitle(); Body.LanguageChanged += UpdateTitle;
		Opened += (_, _) => Body.SetHostVisible(true);
		PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty && !_prepared) Body.SetHostVisible(IsVisible); };
		Closing += OnClosing;
	}

	/// <summary>可嵌入其他原生容器的对话正文。</summary>
	public ChatView Body { get; }
	/// <summary>仅宿主退出流程可允许真正关闭。</summary>
	public bool AllowClose { get; set; }

	/// <summary>先处理录音、待决授权和活动会话，成功后才解除事件订阅。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_prepared) return;
		await Body.PrepareShutdownAsync(); _prepared = true; _service.Dispose();
	}

	/// <summary>清理失败时重新显示正文和原始草稿，允许用户重试。</summary>
	public void ReportHostFailure(Exception exception)
	{
		if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ReportHostFailure(exception)); return; }
		Body.ReportFailure(exception); if (!IsVisible) Show(); Activate();
	}

	private void UpdateTitle() => Title = Body.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Nori · Chat" : "Nori · 对话";
	private async void OnClosing(object? sender, WindowClosingEventArgs args)
	{
		if (AllowClose) return;
		args.Cancel = true;
		if (_closing) return;
		_closing = true;
		try { await Body.PrepareHideAsync(); Hide(); }
		catch (Exception exception) { Body.ReportFailure(exception); }
		finally { _closing = false; }
	}
	protected override void OnClosed(EventArgs e)
	{
		Body.LanguageChanged -= UpdateTitle; Body.Dispose(); _service.Dispose(); base.OnClosed(e);
	}
}
