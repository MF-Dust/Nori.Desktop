using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nori.Desktop.Settings;

internal static class SettingsBrushes
{
	public static IBrush Resolve(Control owner, string key)
	{
		if (owner.Resources.TryGetResource(key, owner.ActualThemeVariant, out object? value) && value is IBrush brush)
			return brush;
		bool dark = owner.ActualThemeVariant == ThemeVariant.Dark;
		return new SolidColorBrush(Color.Parse(key switch
		{
			"SettingsWindowBrush" => dark ? "#202227" : "#F6F7F9",
			"SettingsSidebarBrush" => dark ? "#191B1F" : "#ECEEF2",
			"SettingsCardBrush" => dark ? "#292C32" : "#FFFFFF",
			"SettingsBorderBrush" => dark ? "#3C414B" : "#D9DCE2",
			"SettingsPrimaryBrush" => dark ? "#F2F4F7" : "#20242B",
			"SettingsSecondaryBrush" => dark ? "#A9B0BC" : "#626A78",
			"SettingsErrorBrush" => dark ? "#FFB4AB" : "#B42318",
			"SettingsSelectionBrush" => dark ? "#243B5B" : "#DCEBFF",
			_ => dark ? "#F2F4F7" : "#20242B",
		}));
	}
}

/// <summary>原生设置页支持的编辑器类型。</summary>
public enum SettingsEditorKind
{
	Text,
	Password,
	Multiline,
	Boolean,
	Number,
	Choice,
	Slider,
	Action,
}

/// <summary>一条可随语言切换的设置文案。</summary>
public readonly record struct SettingsText(string Chinese, string English)
{
	/// <summary>按当前语言解析文案。</summary>
	public string Resolve(string language) => language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Chinese;
}

/// <summary>设置页选项。</summary>
public sealed record SettingsOption(string Value, SettingsText Text)
{
	/// <summary>当前语言下显示的选项名称。</summary>
	public string DisplayText { get; internal set; } = Text.Chinese;
}

/// <summary>提供可观察属性通知的设置对象。</summary>
public abstract class SettingsObservableObject : INotifyPropertyChanged
{
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>通知属性变化。</summary>
	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	/// <summary>设置字段并在值改变时发出通知。</summary>
	protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}
}

/// <summary>设置分组中的一组字段。</summary>
public sealed class SettingsSectionViewModel : SettingsObservableObject
{
	private readonly SettingsText _titleText;
	private string _language = "zh-CN";

	/// <summary>创建设置分组。</summary>
	public SettingsSectionViewModel(SettingsText title) => _titleText = title;

	/// <summary>当前语言下的分组标题。</summary>
	public string Title => _titleText.Resolve(_language);

	/// <summary>分组字段。</summary>
	public ObservableCollection<SettingsFieldViewModel> Fields { get; } = [];

	internal void SetLanguage(string language)
	{
		_language = language;
		foreach (SettingsFieldViewModel field in Fields) field.SetLanguage(language);
		OnPropertyChanged(nameof(Title));
	}
}

/// <summary>设置页公共契约，供各功能页并行实现。</summary>
public interface ISettingsPage
{
	/// <summary>稳定页面键。</summary>
	string Key { get; }

	/// <summary>左侧导航分组键。</summary>
	string GroupKey { get; }

	/// <summary>当前语言下的页面标题。</summary>
	string DisplayTitle { get; }

	/// <summary>当前语言下的页面说明。</summary>
	string Description { get; }

	/// <summary>页面字段分组。</summary>
	ObservableCollection<SettingsSectionViewModel> Sections { get; }

	/// <summary>等待本页防抖保存完成。</summary>
	Task<bool> FlushPendingSavesAsync(CancellationToken cancellationToken = default);

	/// <summary>取消查询、等待保存并释放页面订阅。</summary>
	Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>页面导航项。</summary>
public sealed class SettingsPageItemViewModel(SettingsPageBase page) : SettingsObservableObject
{
	/// <summary>页面实例。</summary>
	public SettingsPageBase Page { get; } = page;

	/// <summary>页面键。</summary>
	public string Key => Page.Key;

	/// <summary>当前语言下的页面标题。</summary>
	public string DisplayTitle => Page.DisplayTitle;

	/// <summary>刷新导航标题。</summary>
	internal void Refresh() => OnPropertyChanged(nameof(DisplayTitle));
}

/// <summary>设置左侧导航分组。</summary>
public sealed class SettingsGroupViewModel(SettingsText title, string key) : SettingsObservableObject
{
	private readonly SettingsText _titleText = title;
	private string _language = "zh-CN";

	/// <summary>稳定分组键。</summary>
	public string Key { get; } = key;

	/// <summary>当前语言下的分组标题。</summary>
	public string Title => _titleText.Resolve(_language);

	/// <summary>该分组的页面。</summary>
	public ObservableCollection<SettingsPageItemViewModel> Pages { get; } = [];

	/// <summary>搜索后的可见页面。</summary>
	public ObservableCollection<SettingsPageItemViewModel> VisiblePages { get; } = [];

	internal void SetLanguage(string language)
	{
		_language = language;
		foreach (SettingsPageItemViewModel page in Pages) page.Refresh();
		OnPropertyChanged(nameof(Title));
	}
}

/// <summary>设置页面的公共基类。</summary>
public abstract class SettingsPageBase : SettingsObservableObject, ISettingsPage, IDisposable
{
	private readonly SettingsText _titleText;
	private readonly SettingsText _descriptionText;
	private readonly CancellationTokenSource _lifetimeCts;
	private readonly List<SettingsFieldViewModel> _fields = [];
	private string _language = "zh-CN";
	private string _errorMessage = string.Empty;
	private string _statusMessage = string.Empty;
	private bool _prepared;

	/// <summary>创建设置页面。</summary>
	protected SettingsPageBase(
		SettingsService service,
		string key,
		string groupKey,
		SettingsText title,
		SettingsText description,
		CancellationToken lifetimeToken)
	{
		Service = service ?? throw new ArgumentNullException(nameof(service));
		Key = key;
		GroupKey = groupKey;
		_titleText = title;
		_descriptionText = description;
		_lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
	}

	/// <summary>设置服务。</summary>
	protected SettingsService Service { get; }

	/// <inheritdoc />
	public string Key { get; }

	/// <inheritdoc />
	public string GroupKey { get; }

	/// <inheritdoc />
	public string DisplayTitle => _titleText.Resolve(_language);

	/// <inheritdoc />
	public string Description => _descriptionText.Resolve(_language);

	/// <summary>页面错误提示；保存失败时保留在编辑区下方。</summary>
	public string ErrorMessage
	{
		get => _errorMessage;
		private set => SetProperty(ref _errorMessage, value);
	}

	/// <summary>页面操作状态提示。</summary>
	public string StatusMessage
	{
		get => _statusMessage;
		private set => SetProperty(ref _statusMessage, value);
	}

	/// <inheritdoc />
	public ObservableCollection<SettingsSectionViewModel> Sections { get; } = [];

	/// <summary>页面生命周期取消令牌。</summary>
	protected CancellationToken LifetimeToken => _lifetimeCts.Token;

	/// <summary>添加设置分组。</summary>
	protected SettingsSectionViewModel AddSection(SettingsText title)
	{
		SettingsSectionViewModel section = new(title);
		section.SetLanguage(_language);
		Sections.Add(section);
		return section;
	}

	/// <summary>添加可保存字段。</summary>
	protected SettingsFieldViewModel AddField(
		SettingsSectionViewModel section,
		string key,
		SettingsText label,
		SettingsText description,
		SettingsEditorKind editorKind,
		Func<JsonElement, object?> snapshotReader,
		object? fallback,
		Func<object?, CancellationToken, Task<JsonElement>> save,
		bool secret = false,
		IReadOnlyList<SettingsOption>? options = null,
		double minimum = 0,
		double maximum = 100,
		double increment = 1,
		bool readOnly = false)
	{
		SettingsFieldViewModel field = new(this, key, label, description, editorKind, snapshotReader, fallback, save, secret, options, minimum, maximum, increment, readOnly: readOnly);
		_fields.Add(field);
		section.Fields.Add(field);
		return field;
	}

	/// <summary>添加只执行动作的字段。</summary>
	protected SettingsFieldViewModel AddAction(
		SettingsSectionViewModel section,
		string key,
		SettingsText label,
		SettingsText description,
		ICommand command)
	{
		SettingsFieldViewModel field = new(this, key, label, description, SettingsEditorKind.Action, _ => null, null,
			(_, _) => Task.FromResult(default(JsonElement)), false, null, 0, 1, 1, command: command);
		_fields.Add(field);
		section.Fields.Add(field);
		return field;
	}

	/// <summary>发起页面后台命令并由统一生命周期取消。</summary>
	protected async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
		return await Service.ExecuteAsync(command, args, linked.Token).ConfigureAwait(false);
	}

	/// <summary>应用服务端快照，不覆盖用户正在编辑的字段。</summary>
	internal void ApplySnapshot(JsonElement snapshot)
	{
		foreach (SettingsFieldViewModel field in _fields) field.ApplySnapshot(snapshot);
	}

	/// <summary>更新语言并刷新所有可见文案。</summary>
	internal void SetLanguage(string language)
	{
		_language = language;
		foreach (SettingsSectionViewModel section in Sections) section.SetLanguage(language);
		OnPropertyChanged(nameof(DisplayTitle));
		OnPropertyChanged(nameof(Description));
		OnPropertyChanged(nameof(Sections));
	}

	/// <inheritdoc />
	public virtual async Task<bool> FlushPendingSavesAsync(CancellationToken cancellationToken = default)
	{
		bool[] results = await Task.WhenAll(_fields.Select(field => field.FlushPendingSavesAsync(cancellationToken))).ConfigureAwait(false);
		bool success = results.All(static result => result);
		if (!success && string.IsNullOrWhiteSpace(ErrorMessage)) ErrorMessage = SettingsResources.SaveFailed.Resolve(_language);
		return success;
	}

	/// <inheritdoc />
	public virtual async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
	{
		if (_prepared) return;
		using CancellationTokenSource saveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		saveCts.CancelAfter(TimeSpan.FromSeconds(8));
		bool saved = await FlushPendingSavesAsync(saveCts.Token).ConfigureAwait(false);
		if (!saved)
			throw new InvalidOperationException(ErrorMessage.Length > 0 ? ErrorMessage : SettingsResources.SaveFailed.Resolve(_language));
		// 保存成功后再取消页面查询与动作，避免把正在刷新的编辑值一并取消。
		_lifetimeCts.Cancel();
		_prepared = true;
	}

	internal void ReportSaveFailure(SettingsFieldViewModel field, Exception exception)
	{
		ErrorMessage = field.Label + ": " + exception.Message;
	}

	/// <summary>更新页面操作状态。</summary>
	protected void SetStatus(string message) => StatusMessage = message;

	/// <inheritdoc />
	public void Dispose()
	{
		if (!_prepared) _lifetimeCts.Cancel();
		foreach (SettingsFieldViewModel field in _fields) field.Dispose();
		_lifetimeCts.Dispose();
	}
}

/// <summary>设置字段的编辑状态、快照同步与防抖保存实现。</summary>
public sealed class SettingsFieldViewModel : SettingsObservableObject, IDisposable
{
	private readonly SettingsPageBase _page;
	private readonly SettingsText _labelText;
	private readonly SettingsText _descriptionText;
	private readonly SettingsEditorKind _editorKind;
	private readonly Func<JsonElement, object?> _snapshotReader;
	private readonly object? _fallback;
	private readonly Func<object?, CancellationToken, Task<JsonElement>> _save;
	private readonly SemaphoreSlim _saveGate = new(1, 1);
	private readonly object _saveSync = new();
	private readonly bool _secret;
	private readonly bool _readOnly;
	private CancellationTokenSource? _debounceCts;
	private Task _pendingTask = Task.CompletedTask;
	private bool _suppress;
	private bool _dirty;
	private bool _prepared;
	private string _language = "zh-CN";
	private string _text = string.Empty;
	private bool _boolean;
	private double _number;
	private string _selected = string.Empty;
	private bool _isConfigured;
	private string _errorText = string.Empty;
	private ICommand? _command;

	/// <summary>创建字段。</summary>
	public SettingsFieldViewModel(
		SettingsPageBase page,
		string key,
		SettingsText label,
		SettingsText description,
		SettingsEditorKind editorKind,
		Func<JsonElement, object?> snapshotReader,
		object? fallback,
		Func<object?, CancellationToken, Task<JsonElement>> save,
		bool secret,
		IReadOnlyList<SettingsOption>? options,
		double minimum,
		double maximum,
		double increment,
		bool readOnly = false,
		ICommand? command = null)
	{
		_page = page;
		Key = key;
		_labelText = label;
		_descriptionText = description;
		_editorKind = editorKind;
		_snapshotReader = snapshotReader;
		_fallback = fallback;
		_save = save;
		_secret = secret;
		_readOnly = readOnly;
		Options = new ObservableCollection<SettingsOption>(options ?? []);
		Minimum = minimum;
		Maximum = maximum;
		Increment = increment;
		_command = command;
		foreach (SettingsOption option in Options) option.DisplayText = option.Text.Chinese;
		ApplyValue(fallback, configured: false);
	}

	/// <summary>稳定字段键。</summary>
	public string Key { get; }

	/// <summary>字段类型。</summary>
	public SettingsEditorKind EditorKind => _editorKind;

	/// <summary>当前语言下字段标题。</summary>
	public string Label => _labelText.Resolve(_language);

	/// <summary>当前语言下字段说明。</summary>
	public string Description => _descriptionText.Resolve(_language);

	/// <summary>文本编辑值。</summary>
	public string Text
	{
		get => _text;
		set
		{
			if (!SetProperty(ref _text, value ?? string.Empty) || _suppress || _readOnly) return;
			QueueSave();
		}
	}

	/// <summary>布尔编辑值。</summary>
	public bool Boolean
	{
		get => _boolean;
		set
		{
			if (!SetProperty(ref _boolean, value) || _suppress || _readOnly) return;
			QueueSave();
		}
	}

	/// <summary>数字编辑值。</summary>
	public double Number
	{
		get => _number;
		set
		{
			if (!SetProperty(ref _number, value) || _suppress || _readOnly) return;
			QueueSave();
		}
	}

	/// <summary>选择编辑值。</summary>
	public string Selected
	{
		get => _selected;
		set
		{
			if (!SetProperty(ref _selected, value ?? string.Empty) || _suppress || _readOnly) return;
			QueueSave();
		}
	}

	/// <summary>选项列表。</summary>
	public ObservableCollection<SettingsOption> Options { get; }

	/// <summary>数值最小值。</summary>
	public double Minimum { get; }

	/// <summary>数值最大值。</summary>
	public double Maximum { get; }

	/// <summary>数值步进。</summary>
	public double Increment { get; }

	/// <summary>密钥是否已保存。</summary>
	public bool IsConfigured
	{
		get => _isConfigured;
		private set => SetProperty(ref _isConfigured, value);
	}

	/// <summary>保存错误，供表单直接显示。</summary>
	public string ErrorText
	{
		get => _errorText;
		private set => SetProperty(ref _errorText, value);
	}

	/// <summary>动作字段命令。</summary>
	public ICommand? Command
	{
		get => _command;
		set => SetProperty(ref _command, value);
	}

	/// <summary>字段是否有未保存编辑。</summary>
	public bool IsDirty => _dirty;

	/// <summary>只读字段不会触发保存。</summary>
	public bool IsReadOnly => _readOnly;

	internal void SetLanguage(string language)
	{
		_language = language;
		foreach (SettingsOption option in Options) option.DisplayText = option.Text.Resolve(language);
		OnPropertyChanged(nameof(Label));
		OnPropertyChanged(nameof(Description));
		OnPropertyChanged(nameof(Options));
	}

	internal void ApplySnapshot(JsonElement snapshot)
	{
		if (_dirty || _prepared) return;
		object? value;
		try { value = _snapshotReader(snapshot) ?? _fallback; }
		catch { value = _fallback; }
		bool configured = value is not null && (value is not string text || !string.IsNullOrEmpty(text));
		ApplyValue(value, configured);
	}

	private void ApplyValue(object? value, bool configured)
	{
		_suppress = true;
		try
		{
			switch (_editorKind)
			{
				case SettingsEditorKind.Boolean:
					_boolean = value switch { bool boolean => boolean, string text => ParseBool(text), _ => Convert.ToBoolean(value ?? false) };
					OnPropertyChanged(nameof(Boolean));
					break;
				case SettingsEditorKind.Number:
				case SettingsEditorKind.Slider:
					_number = value switch { double number => number, float single => single, int integer => integer, long longValue => longValue, string text when double.TryParse(text, out double parsed) => parsed, _ => Convert.ToDouble(value ?? 0) };
					OnPropertyChanged(nameof(Number));
					break;
				case SettingsEditorKind.Choice:
					_selected = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
					OnPropertyChanged(nameof(Selected));
					break;
				default:
					_text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
					OnPropertyChanged(nameof(Text));
					break;
			}
			IsConfigured = configured;
			_dirty = false;
			OnPropertyChanged(nameof(IsDirty));
		}
		finally { _suppress = false; }
	}

	private static bool ParseBool(string value) => value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase);

	private object? ReadValue() => _editorKind switch
	{
		SettingsEditorKind.Boolean => _boolean,
		SettingsEditorKind.Number or SettingsEditorKind.Slider => _number,
		SettingsEditorKind.Choice => _selected,
		_ => _text,
	};

	private void QueueSave()
	{
		if (_prepared) return;
		_dirty = true;
		ErrorText = string.Empty;
		OnPropertyChanged(nameof(IsDirty));
		lock (_saveSync)
		{
			_debounceCts?.Cancel();
			_debounceCts?.Dispose();
			_debounceCts = new CancellationTokenSource();
			CancellationToken token = _debounceCts.Token;
			_pendingTask = DebounceAndSaveAsync(token);
		}
	}

	private async Task DebounceAndSaveAsync(CancellationToken cancellationToken)
	{
		try { await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return; }
		await SaveNowAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>立即串行保存字段。</summary>
	public async Task<bool> SaveNowAsync(CancellationToken cancellationToken = default)
	{
		if (!_dirty || _prepared) return !_dirty;
		await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_dirty || _prepared) return !_dirty;
			await _save(ReadValue(), cancellationToken).ConfigureAwait(false);
			_dirty = false;
			ErrorText = string.Empty;
			IsConfigured = _secret ? true : IsConfigured;
			if (_secret)
			{
				_suppress = true;
				try { _text = string.Empty; }
				finally { _suppress = false; }
				OnPropertyChanged(nameof(Text));
			}
			OnPropertyChanged(nameof(IsDirty));
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (Exception exception)
		{
			ErrorText = exception.Message;
			_page.ReportSaveFailure(this, exception);
			return false;
		}
		finally { _saveGate.Release(); }
	}

	/// <inheritdoc />
	public async Task<bool> FlushPendingSavesAsync(CancellationToken cancellationToken = default)
	{
		Task pending;
		lock (_saveSync)
		{
			_debounceCts?.Cancel();
			pending = _pendingTask;
		}
		try { await pending.ConfigureAwait(false); }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		if (!_dirty) return true;
		return await SaveNowAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_prepared) return;
		_prepared = true;
		lock (_saveSync)
		{
			_debounceCts?.Cancel();
			_debounceCts?.Dispose();
			_debounceCts = null;
		}
		_saveGate.Dispose();
	}
}

/// <summary>设置页面配置键与文案。</summary>
public static class SettingsResources
{
	/// <summary>应用窗口标题。</summary>
	public static readonly SettingsText WindowTitle = new("Nori 设置", "Nori Settings");
	/// <summary>搜索框占位文案。</summary>
	public static readonly SettingsText SearchPlaceholder = new("搜索设置", "Search settings");
	/// <summary>保存失败文案。</summary>
	public static readonly SettingsText SaveFailed = new("设置保存失败，请检查输入后重试。", "Settings could not be saved. Check the input and retry.");
	/// <summary>设置分组文案。</summary>
	public static readonly SettingsText CoreGroup = new("核心", "Core");
	/// <summary>感知分组文案。</summary>
	public static readonly SettingsText PerceptionGroup = new("感知", "Perception");
	/// <summary>扩展分组文案。</summary>
	public static readonly SettingsText ExtendGroup = new("扩展", "Extensions");
	/// <summary>系统分组文案。</summary>
	public static readonly SettingsText SystemGroup = new("系统", "System");
}
