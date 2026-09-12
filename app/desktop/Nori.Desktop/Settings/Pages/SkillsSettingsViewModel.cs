using System.Collections.ObjectModel;
using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>技能条目的脱敏本地表示。</summary>
public sealed record SkillItem(
	string Id,
	string Name,
	string Description,
	string Author,
	string Version,
	string Icon,
	IReadOnlyList<string> Tags,
	string Category,
	string Instructions,
	bool Enabled,
	string Source,
	IReadOnlyList<string> Tools,
	long InstalledAt,
	string? Url);

/// <summary>技能编辑表单。</summary>
public sealed record SkillDraft(
	string Id,
	string Name,
	string Description,
	string Author,
	string Version,
	string Icon,
	IReadOnlyList<string> Tags,
	string Category,
	string Instructions,
	IReadOnlyList<string> Tools,
	bool Enabled,
	string Source,
	long InstalledAt,
	string? Url);

/// <summary>技能工坊设置页的状态与宿主命令编排。</summary>
public sealed class SkillsSettingsViewModel : SettingsPageViewModelBase
{
	private static readonly string[] Categories = ["all", "productivity", "coding", "life", "roleplay", "entertainment"];
	private IReadOnlyList<SkillItem> _installed = [];
	private IReadOnlyList<SkillItem> _marketplace = [];
	private string _searchText = "";
	private string _selectedCategory = "all";
	private bool _showMarketplace;

	/// <summary>创建技能工坊 ViewModel。</summary>
	public SkillsSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>已安装技能。</summary>
	public IReadOnlyList<SkillItem> Installed
	{
		get => _installed;
		private set => SetProperty(ref _installed, value);
	}

	/// <summary>市场技能。</summary>
	public IReadOnlyList<SkillItem> Marketplace
	{
		get => _marketplace;
		private set => SetProperty(ref _marketplace, value);
	}

	/// <summary>搜索文本。</summary>
	public string SearchText
	{
		get => _searchText;
		set
		{
			if (!SetProperty(ref _searchText, value ?? "")) return;
			NotifyChanged(nameof(FilteredInstalled));
			NotifyChanged(nameof(FilteredMarketplace));
		}
	}

	/// <summary>当前分类。</summary>
	public string SelectedCategory
	{
		get => _selectedCategory;
		set
		{
			string next = Categories.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : "all";
			if (!SetProperty(ref _selectedCategory, next)) return;
			NotifyChanged(nameof(FilteredInstalled));
			NotifyChanged(nameof(FilteredMarketplace));
		}
	}

	/// <summary>当前是否显示市场。</summary>
	public bool ShowMarketplace
	{
		get => _showMarketplace;
		set => SetProperty(ref _showMarketplace, value);
	}

	/// <summary>可选分类键。</summary>
	public static IReadOnlyList<string> CategoryKeys => Categories;

	/// <summary>过滤后的已安装技能。</summary>
	public IReadOnlyList<SkillItem> FilteredInstalled => Filter(Installed);

	/// <summary>过滤后的市场技能。</summary>
	public IReadOnlyList<SkillItem> FilteredMarketplace => Filter(Marketplace);

	/// <summary>已安装技能 ID 集合。</summary>
	public IReadOnlySet<string> InstalledIds => Installed.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			Installed = ParseSkills(SettingsJson.Array(snapshot, "skills"));
			JsonElement marketplace = await Service.ExecuteAsync("skills_marketplace", cancellationToken: cancellationToken).ConfigureAwait(true);
			Marketplace = ParseSkills(SettingsJson.RootArray(marketplace), marketplace: true);
			NotifyChanged(nameof(FilteredInstalled));
			NotifyChanged(nameof(FilteredMarketplace));
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>切换技能启用状态。</summary>
	public async Task ToggleAsync(SkillItem skill, bool enabled, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(skill);
		await ExecuteAsync("skills_toggle", new {id = skill.Id, enabled}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>安装市场技能。</summary>
	public async Task InstallMarketplaceAsync(SkillItem skill, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(skill);
		await ExecuteAsync("skills_install_marketplace", new {skillId = skill.Id}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>从远程 URL 安装技能。</summary>
	public async Task InstallUrlAsync(string url, CancellationToken cancellationToken = default)
	{
		if (!TryValidateHttpUrl(url, out string normalized)) throw new ArgumentException("技能 URL 必须是公开的 HTTP 或 HTTPS 地址。", nameof(url));
		await ExecuteAsync("skills_install_url", new {url = normalized}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>保存或更新自定义技能。</summary>
	public async Task SaveCustomAsync(SkillDraft draft, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(draft);
		if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("技能名称不能为空。", nameof(draft));
		if (string.IsNullOrWhiteSpace(draft.Instructions)) throw new ArgumentException("技能指令不能为空。", nameof(draft));
		object skill = new
		{
			id = draft.Id,
			name = draft.Name.Trim(),
			description = draft.Description.Trim(),
			author = draft.Author.Trim(),
			version = draft.Version.Trim(),
			icon = draft.Icon,
			tags = draft.Tags,
			category = draft.Category.Trim(),
			instructions = draft.Instructions,
			tools = draft.Tools,
			enabled = draft.Enabled,
			source = string.IsNullOrWhiteSpace(draft.Source) ? "custom" : draft.Source,
			installedAt = draft.InstalledAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : draft.InstalledAt,
			url = draft.Url,
		};
		await ExecuteAsync("skills_save_custom", new {skill}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>卸载非内置技能。</summary>
	public async Task UninstallAsync(SkillItem skill, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(skill);
		if (string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("内置技能不能卸载。 ");
		await ExecuteAsync("skills_uninstall", new {id = skill.Id}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>读取技能的完整指令正文。</summary>
	public async Task<SkillItem> LoadDetailsAsync(SkillItem skill, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(skill);
		JsonElement result = await ExecuteAsync("skills_export", new {id = skill.Id}, cancellationToken).ConfigureAwait(true);
		string raw = SettingsJson.RawOrString(result);
		JsonElement? exported = SettingsJson.TryParse(raw);
		if (exported is not { } document) return skill;
		string instructions = SettingsJson.String(document, "instructions", skill.Instructions);
		return skill with {Instructions = instructions, Tools = ReadStrings(document, "tools", skill.Tools)};
	}

	/// <summary>创建一个新的技能草稿。</summary>
	public static SkillDraft NewDraft(string author)
	{
		return new SkillDraft(
			$"skill_custom_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
			"",
			"",
			author,
			"1.0.0",
			"sparkles",
			[],
			"productivity",
			"",
			[],
			true,
			"custom",
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			null);
	}

	/// <summary>从当前条目生成可编辑草稿。</summary>
	public static SkillDraft ToDraft(SkillItem item) => new(
		item.Id,
		item.Name,
		item.Description,
		item.Author,
		item.Version,
		item.Icon,
		item.Tags,
		item.Category,
		item.Instructions,
		item.Tools,
		item.Enabled,
		item.Source,
		item.InstalledAt,
		item.Url);

	private IReadOnlyList<SkillItem> Filter(IReadOnlyList<SkillItem> items)
	{
		string query = SearchText.Trim();
		return items.Where(item =>
			(SelectedCategory == "all" || string.Equals(item.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
			&& (query.Length == 0
				|| item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))))
			.ToArray();
	}

	private static IReadOnlyList<SkillItem> ParseSkills(IEnumerable<JsonElement> values, bool marketplace = false) =>
		values.Select(item => ParseSkill(item, marketplace)).Where(item => item.Id.Length > 0).ToArray();

	private static SkillItem ParseSkill(JsonElement item, bool marketplace)
	{
		return new SkillItem(
			SettingsJson.String(item, "id"),
			SettingsJson.String(item, "name"),
			SettingsJson.String(item, "description"),
			SettingsJson.String(item, "author"),
			SettingsJson.String(item, "version", "1.0.0"),
			SettingsJson.String(item, "icon", "sparkles"),
			ReadStrings(item, "tags"),
			SettingsJson.String(item, "category", "productivity"),
			SettingsJson.String(item, "instructions"),
			marketplace ? false : SettingsJson.Bool(item, "enabled", true),
			SettingsJson.String(item, "source", marketplace ? "market" : "custom"),
			ReadStrings(item, "tools"),
			SettingsJson.Long(item, "installedAt"),
			SettingsJson.NullableString(item, "url"));
	}

	private static IReadOnlyList<string> ReadStrings(JsonElement value, string name, IReadOnlyList<string>? fallback = null)
	{
		JsonElement array = SettingsJson.Property(value, name);
		if (array.ValueKind != JsonValueKind.Array) return fallback ?? [];
		return array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray();
	}

	private static bool TryValidateHttpUrl(string? value, out string normalized)
	{
		normalized = value?.Trim() ?? "";
		return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme is "http" or "https"
			&& uri.Host.Length > 0;
	}
}
