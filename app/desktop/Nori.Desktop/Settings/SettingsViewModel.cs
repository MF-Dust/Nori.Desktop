using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Settings;

/// <summary>原生设置窗口的导航与页面状态。</summary>
public sealed class SettingsViewModel : SettingsObservableObject, IDisposable
{
	private static readonly (string Key, SettingsText Title)[] GroupOrder =
	[
		("core", new("核心", "Core")),
		("perception", new("感知", "Perception")),
		("extend", new("扩展", "Extensions")),
		("system", new("系统", "System")),
	];

	private readonly SettingsService _service;
	private readonly CancellationTokenSource _lifetimeCts = new();
	private readonly Dictionary<string, SettingsPageBase> _pages = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SettingsGroupViewModel> _groups = new(StringComparer.Ordinal);
	private string _language = "zh-CN";
	private string _searchText = string.Empty;
	private SettingsPageBase? _currentPage;
	private string _errorMessage = string.Empty;
	private bool _disposed;
	private Task? _refreshTask;
	private bool _refreshPending;

	/// <summary>创建设置窗口状态。</summary>
	public SettingsViewModel(SettingsService service)
	{
		_service = service ?? throw new ArgumentNullException(nameof(service));
		foreach ((string key, SettingsText title) in GroupOrder)
		{
			SettingsGroupViewModel group = new(title, key);
			_groups[key] = group;
			Groups.Add(group);
		}

		NavigateCommand = new SettingsCommand(parameter => Navigate(parameter as string));
		AddPage(new AiSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new VoiceSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new ProactiveSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new GeneralSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new UpdatesSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new AboutSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new SkillsSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new McpSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new AutomationSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new PluginsSettingsPage(_service, _lifetimeCts.Token));
		AddPage(new DebugSettingsPage(_service, _lifetimeCts.Token));
		_service.StateChanged += OnStateChanged;
	}

	/// <summary>所有导航分组。</summary>
	public ObservableCollection<SettingsGroupViewModel> Groups { get; } = [];

	/// <summary>搜索过滤后的导航分组。</summary>
	public ObservableCollection<SettingsGroupViewModel> FilteredGroups { get; } = [];

	/// <summary>当前页面。</summary>
	public SettingsPageBase? CurrentPage
	{
		get => _currentPage;
		private set
		{
			if (ReferenceEquals(_currentPage, value)) return;
			_currentPage = value;
			foreach (SettingsGroupViewModel group in Groups)
				foreach (SettingsPageItemViewModel item in group.Pages)
					item.IsSelected = ReferenceEquals(item.Page, value);
			OnPropertyChanged();
			OnPropertyChanged(nameof(HorizontalScrollBarVisibility));
			OnPropertyChanged(nameof(CurrentPageTitle));
			OnPropertyChanged(nameof(CurrentPageDescription));
			OnPropertyChanged(nameof(CurrentPageError));
		}
	}

	/// <summary>诊断文本按视口换行，其它复杂表单保留横向滚动兜底。</summary>
	public ScrollBarVisibility HorizontalScrollBarVisibility =>
		CurrentPage is DebugSettingsPage ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

	/// <summary>当前页面标题。</summary>
	public string CurrentPageTitle => CurrentPage?.DisplayTitle ?? string.Empty;

	/// <summary>当前页面说明。</summary>
	public string CurrentPageDescription => CurrentPage?.Description ?? string.Empty;

	/// <summary>当前页面错误。</summary>
	public string CurrentPageError => CurrentPage?.ErrorMessage ?? ErrorMessage;

	/// <summary>窗口标题。</summary>
	public string WindowTitle => SettingsResources.WindowTitle.Resolve(_language);

	/// <summary>搜索框占位文案。</summary>
	public string SearchPlaceholder => SettingsResources.SearchPlaceholder.Resolve(_language);

	/// <summary>当前语言。</summary>
	public string Language
	{
		get => _language;
		private set
		{
			if (!SetProperty(ref _language, value)) return;
			foreach (SettingsGroupViewModel group in Groups) group.SetLanguage(value);
			foreach (SettingsPageBase page in _pages.Values) page.SetLanguage(value);
			SettingsLocalization.SetLanguage(value);
			RefreshFilteredGroups();
			OnPropertyChanged(nameof(WindowTitle));
			OnPropertyChanged(nameof(SearchPlaceholder));
			OnPropertyChanged(nameof(CurrentPageTitle));
			OnPropertyChanged(nameof(CurrentPageDescription));
		}
	}

	/// <summary>搜索关键词。</summary>
	public string SearchText
	{
		get => _searchText;
		set
		{
			if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
			RefreshFilteredGroups();
		}
	}

	/// <summary>统一导航命令。</summary>
	public ICommand NavigateCommand { get; }

	/// <summary>页面级错误提示。</summary>
	public string ErrorMessage
	{
		get => _errorMessage;
		private set
		{
			if (!SetProperty(ref _errorMessage, value)) return;
			OnPropertyChanged(nameof(CurrentPageError));
		}
	}

	/// <summary>注册并显示一个并行实现的设置页。</summary>
	public void AddPage(SettingsPageBase page)
	{
		ArgumentNullException.ThrowIfNull(page);
		if (!_groups.TryGetValue(page.GroupKey, out SettingsGroupViewModel? group))
			throw new ArgumentException("设置页分组不存在：" + page.GroupKey, nameof(page));
		if (_pages.ContainsKey(page.Key)) return;
		_pages.Add(page.Key, page);
		group.Pages.Add(new SettingsPageItemViewModel(page) {IsSelected = ReferenceEquals(CurrentPage, page)});
		page.SetLanguage(Language);
		if (CurrentPage is null) CurrentPage = page;
		RefreshFilteredGroups();
	}

	/// <summary>切换设置页。</summary>
	public void Navigate(string? page)
	{
		if (_disposed) return;
		if (!string.IsNullOrWhiteSpace(page) && _pages.TryGetValue(page, out SettingsPageBase? selected))
		{
			bool changed = !ReferenceEquals(CurrentPage, selected);
			CurrentPage = selected;
			if (changed) _ = RefreshSnapshotAsync(_lifetimeCts.Token);
			return;
		}
		if (CurrentPage is null) CurrentPage = _pages.Values.FirstOrDefault();
	}

	/// <summary>刷新服务端快照。</summary>
	public async Task RefreshSnapshotAsync(CancellationToken cancellationToken = default)
	{
		// 服务通知可能来自后台；绑定状态与页面控件统一在 UI 线程更新。
		if (!Dispatcher.UIThread.CheckAccess())
		{
			await Dispatcher.UIThread.InvokeAsync(() => RefreshSnapshotAsync(cancellationToken));
			return;
		}
		if (_disposed) return;
		_refreshPending = true;
		// 多个通知共享同一条刷新链，进行中的读取结束后最多补读一次最新状态。
		if (_refreshTask is null || _refreshTask.IsCompleted)
			_refreshTask = RefreshSnapshotsCoreAsync();
		await _refreshTask.WaitAsync(cancellationToken).ConfigureAwait(true);
	}

	private async Task RefreshSnapshotsCoreAsync()
	{
		while (_refreshPending && !_disposed)
		{
			_refreshPending = false;
			try
			{
				JsonElement snapshot = await _service.GetSnapshotAsync(_lifetimeCts.Token).ConfigureAwait(true);
				if (_disposed) return;
				string language = SettingsSnapshotReader.String(snapshot, Language, "general", "language");
				if (language is not ("zh-CN" or "en-US")) language = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
				Language = language;
				foreach (SettingsPageBase page in _pages.Values) page.ApplySnapshot(snapshot);
				// 未显示的复杂页面在导航时读取，避免每次运行时变化都查询所有宿主服务。
				if (CurrentPage is NativeSettingsPageBase complex)
					await complex.RefreshComplexAsync(_lifetimeCts.Token).ConfigureAwait(true);
				ErrorMessage = string.Empty;
			}
			catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { return; }
			catch (Exception exception)
			{
				ErrorMessage = exception.Message;
			}
		}
	}

	/// <summary>等待所有字段防抖保存结束。</summary>
	public async Task<bool> FlushPendingSavesAsync(CancellationToken cancellationToken = default)
	{
		bool[] results = await Task.WhenAll(_pages.Values.Select(page => page.FlushPendingSavesAsync(cancellationToken))).ConfigureAwait(false);
		await Task.WhenAll(_pages.Values.OfType<NativeSettingsPageBase>().Select(page => page.FlushComplexAsync(cancellationToken))).ConfigureAwait(false);
		bool success = results.All(static result => result);
		if (!success) ErrorMessage = SettingsResources.SaveFailed.Resolve(Language);
		return success;
	}

	/// <summary>取消查询、等待保存并释放页面。</summary>
	public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
	{
		if (_disposed) return;
		_service.StateChanged -= OnStateChanged;
		using CancellationTokenSource saveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		saveCts.CancelAfter(TimeSpan.FromSeconds(8));
		bool saved;
		try
		{
			saved = await FlushPendingSavesAsync(saveCts.Token).ConfigureAwait(false);
		}
		catch
		{
			_service.StateChanged += OnStateChanged;
			throw;
		}
		if (!saved)
		{
			_service.StateChanged += OnStateChanged;
			throw new InvalidOperationException(ErrorMessage.Length > 0 ? ErrorMessage : SettingsResources.SaveFailed.Resolve(Language));
		}
		// 字段落盘后再取消页面查询，保证关闭时不会因共享令牌取消最后一次保存。
		_lifetimeCts.Cancel();
		if (_refreshTask is not null) await _refreshTask.ConfigureAwait(false);
		foreach (SettingsPageBase page in _pages.Values) await page.PrepareShutdownAsync(saveCts.Token).ConfigureAwait(false);
		_disposed = true;
	}

	private void OnStateChanged()
	{
		if (_disposed) return;
		_ = RefreshSnapshotAsync(_lifetimeCts.Token);
	}

	private void RefreshFilteredGroups()
	{
		string needle = SearchText.Trim();
		FilteredGroups.Clear();
		foreach (SettingsGroupViewModel group in Groups)
		{
			group.VisiblePages.Clear();
			foreach (SettingsPageItemViewModel item in group.Pages)
			{
				bool visible = needle.Length == 0 || Matches(item.Page, needle);
				if (visible) group.VisiblePages.Add(item);
			}
			if (group.VisiblePages.Count > 0) FilteredGroups.Add(group);
		}
	}

	private static bool Matches(SettingsPageBase page, string needle)
	{
		if (Contains(page.Key) || Contains(page.DisplayTitle) || Contains(page.Description)) return true;
		return page.Sections.Any(section =>
			Contains(section.Title) ||
			section.Fields.Any(field => Contains(field.Label) || Contains(field.Description) || Contains(field.Key)));

		bool Contains(string value) => value.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_lifetimeCts.Cancel();
		_service.StateChanged -= OnStateChanged;
		foreach (SettingsPageBase page in _pages.Values) page.Dispose();
		_lifetimeCts.Dispose();
	}
}
