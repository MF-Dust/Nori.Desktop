using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;
using Nori.Desktop.Ui;

namespace Nori.Desktop.Settings.Pages;

public sealed partial class NativeSettingsPagePresenter
{
	private void BuildSkills(StackPanel root, SkillsSettingsViewModel viewModel)
	{
		int sync = 0;
		ToggleButton installed = Choice(NativeSettingsResources.Get("skills.installed"));
		ToggleButton marketplace = Choice(NativeSettingsResources.Get("skills.marketplace"));
		installed.IsChecked = !viewModel.ShowMarketplace;
		marketplace.IsChecked = viewModel.ShowMarketplace;
		installed.IsCheckedChanged += (_, _) => OnSegment(installed, !viewModel.ShowMarketplace, () => viewModel.ShowMarketplace = false);
		marketplace.IsCheckedChanged += (_, _) => OnSegment(marketplace, viewModel.ShowMarketplace, () => viewModel.ShowMarketplace = true);
		Button url = Button(NativeSettingsResources.Get("skills.installUrl"), () => _ = RunAsync(() => InstallSkillUrlAsync(viewModel)), enabled: !viewModel.SafeMode);
		root.Children.Add(ActionGroup(installed, marketplace,
			Button(NativeSettingsResources.Get("skills.new"), () => _ = RunAsync(() => NewSkillAsync(viewModel)), accent: true),
			Button(NativeSettingsResources.Get("skills.import"), () => _ = RunAsync(() => NativeCatalogImportDialogs.ShowSkillImportAsync(Owner(), viewModel))),
			url,
			Button(NativeSettingsResources.Get("common.refresh"), () => _ = RunAsync(() => viewModel.RefreshAsync()))));
		Grid filters = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8};
		TextBox search = new()
		{
			Name = "SkillSearch",
			Text = viewModel.SearchText,
			PlaceholderText = NativeSettingsResources.Get("skills.search"),
			MinWidth = 80,
		};
		Avalonia.Automation.AutomationProperties.SetName(search, NativeSettingsResources.Get("skills.search"));
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? "";
		filters.Children.Add(search);
		ComboBox category = new()
		{
			Name = "SkillCategory",
			ItemsSource = SkillsSettingsViewModel.CategoryKeys.Select(CategoryName).ToArray(),
			SelectedIndex = Math.Max(0, Array.IndexOf(SkillsSettingsViewModel.CategoryKeys.ToArray(), viewModel.SelectedCategory)),
			MinWidth = 148,
		};
		category.SelectionChanged += (_, _) =>
		{
			if (sync > 0 || category.SelectedIndex < 0) return;
			viewModel.SelectedCategory = SkillsSettingsViewModel.CategoryKeys[category.SelectedIndex];
		};
		Grid.SetColumn(category, 1);
		filters.Children.Add(category);
		root.Children.Add(filters);
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new
			{
				viewModel.ShowMarketplace,
				viewModel.SearchText,
				viewModel.SelectedCategory,
				viewModel.SafeMode,
				viewModel.EnabledCount,
				viewModel.PromptCharacters,
				viewModel.PromptLimit,
				viewModel.Installed,
				viewModel.Marketplace,
			}),
			list =>
			{
				sync++;
				installed.IsChecked = !viewModel.ShowMarketplace;
				marketplace.IsChecked = viewModel.ShowMarketplace;
				int categoryIndex = Array.IndexOf(SkillsSettingsViewModel.CategoryKeys.ToArray(), viewModel.SelectedCategory);
				if (categoryIndex >= 0 && category.SelectedIndex != categoryIndex) category.SelectedIndex = categoryIndex;
				sync--;
				url.IsEnabled = !viewModel.SafeMode;
				list.Children.Add(SkillSummary(viewModel));
				IReadOnlyList<SkillItem> items = viewModel.ShowMarketplace ? viewModel.FilteredMarketplace : viewModel.FilteredInstalled;
				if (items.Count == 0) list.Children.Add(Empty(SkillEmptyText(viewModel)));
				else list.Children.Add(CardGrid(items.Select(skill => SkillCard(viewModel, skill))));
			});

		void OnSegment(ToggleButton button, bool selected, Action select)
		{
			if (sync > 0) return;
			if (button.IsChecked == true)
			{
				select();
				return;
			}
			if (!selected) return;
			sync++;
			button.IsChecked = true;
			sync--;
		}
	}

	private Control SkillSummary(SkillsSettingsViewModel viewModel)
	{
		bool over = viewModel.PromptOverBudget;
		string count = string.Format(NativeSettingsResources.Get("skills.enabledCount"), viewModel.EnabledCount, viewModel.SkillLimit);
		string budget = string.Format(NativeSettingsResources.Get("skills.promptBudget"), viewModel.PromptCharacters, viewModel.PromptLimit);
		TextBlock text = new()
		{
			Text = over ? $"{count} · {budget} · {NativeSettingsResources.Get("skills.overBudget")}" : $"{count} · {budget}",
			TextWrapping = TextWrapping.Wrap,
			Foreground = Brush(over ? "SettingsWarningBrush" : "SettingsSecondaryBrush"),
		};
		return text;
	}

	private static string SkillEmptyText(SkillsSettingsViewModel viewModel)
	{
		bool filtered = viewModel.SearchText.Trim().Length > 0 || !string.Equals(viewModel.SelectedCategory, "all", StringComparison.OrdinalIgnoreCase);
		if (filtered) return NativeSettingsResources.Get("skills.emptyFilter");
		return viewModel.ShowMarketplace
			? NativeSettingsResources.Get("skills.emptyMarket")
			: NativeSettingsResources.Get("skills.emptyInstalled");
	}

	private async Task NewSkillAsync(SkillsSettingsViewModel viewModel)
	{
		await NativeSkillEditor.ShowAsync(Owner(), viewModel, SkillsSettingsViewModel.NewDraft(Environment.UserName)).ConfigureAwait(true);
	}

	private async Task InstallSkillUrlAsync(SkillsSettingsViewModel viewModel)
	{
		if (viewModel.SafeMode) return;
		string? url = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("skills.installUrl"), NativeSettingsResources.Get("skills.url")).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(url)) await viewModel.InstallUrlAsync(url).ConfigureAwait(true);
	}

	private async Task EditSkillAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		if (string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase))
		{
			await viewModel.DuplicateAsync(skill).ConfigureAwait(true);
			return;
		}
		SkillItem detailed = await viewModel.LoadDetailsAsync(skill).ConfigureAwait(true);
		await NativeSkillEditor.ShowAsync(Owner(), viewModel, SkillsSettingsViewModel.ToDraft(detailed)).ConfigureAwait(true);
	}

	private async Task ShowSkillDetailsAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		SkillItem detailed = await viewModel.LoadDetailsAsync(skill).ConfigureAwait(true);
		string text = string.IsNullOrWhiteSpace(detailed.Instructions) ? NativeSettingsResources.Get("common.none") : detailed.Instructions;
		if (detailed.Tools.Count > 0) text += Environment.NewLine + Environment.NewLine + $"{NativeSettingsResources.Get("skills.tools")}: {string.Join(", ", detailed.Tools)}";
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), detailed.Name, text).ConfigureAwait(true);
	}

	private async Task UninstallSkillAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		if (string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase))
		{
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), skill.Name, NativeSettingsResources.Get("skills.builtin")).ConfigureAwait(true);
			return;
		}
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("skills.uninstallConfirm"), true).ConfigureAwait(true))
			await viewModel.UninstallAsync(skill).ConfigureAwait(true);
	}

	private Control SkillCard(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		StackPanel body = new() {Spacing = 8};
		body.Children.Add(TitleRow(skill.Icon, skill.Name, SourceBadge(skill.Source)));
		body.Children.Add(new TextBlock
		{
			Text = $"{skill.Version} · {skill.Author} · {CategoryName(skill.Category)}",
			Foreground = Brush("SettingsSecondaryBrush"),
			TextWrapping = TextWrapping.Wrap,
		});
		if (!string.IsNullOrWhiteSpace(skill.Description))
			body.Children.Add(new TextBlock {Text = skill.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")});
		if (skill.Tags.Count > 0) body.Children.Add(TagRow(skill.Tags));
		WrapPanel actions = new() {Margin = new Thickness(0, 4, 0, 0)};
		if (viewModel.ShowMarketplace)
		{
			if (viewModel.InstalledIds.Contains(skill.Id))
				actions.Children.Add(Pill(NativeSettingsResources.Get("skills.installedBadge"), "SettingsSuccessBrush"));
			else
				actions.Children.Add(Button(NativeSettingsResources.Get("common.install"), () => _ = RunAsync(() => viewModel.InstallMarketplaceAsync(skill)), accent: true));
		}
		else
		{
			ToggleSwitch enabled = new() {IsChecked = skill.Enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
			Avalonia.Automation.AutomationProperties.SetName(enabled, skill.Name);
			enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleAsync(skill, enabled.IsChecked == true));
			actions.Children.Add(enabled);
			actions.Children.Add(Button(NativeSettingsResources.Get("skills.details"), () => _ = RunAsync(() => ShowSkillDetailsAsync(viewModel, skill))));
			bool builtin = string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase);
			actions.Children.Add(Button(builtin ? NativeSettingsResources.Get("skills.copyCustom") : NativeSettingsResources.Get("common.edit"),
				() => _ = RunAsync(() => builtin ? viewModel.DuplicateAsync(skill) : EditSkillAsync(viewModel, skill))));
			if (!builtin)
			{
				actions.Children.Add(Button(NativeSettingsResources.Get("skills.duplicate"), () => _ = RunAsync(() => viewModel.DuplicateAsync(skill))));
				actions.Children.Add(Button(NativeSettingsResources.Get("common.export"), () => _ = RunAsync(() => NativeCatalogImportDialogs.ShowSkillExportAsync(Owner(), viewModel, skill))));
				actions.Children.Add(Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallSkillAsync(viewModel, skill)), danger: true));
			}
		}
		foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 6, 4);
		body.Children.Add(actions);
		return WrapCard(body, compact: true);
	}

	private Control TitleRow(string icon, string title, Control badge)
	{
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8};
		row.Children.Add(IconBadge(icon));
		TextBlock heading = new()
		{
			Text = title,
			FontSize = NoriMetrics.FontBase,
			FontWeight = FontWeight.SemiBold,
			Foreground = Brush("SettingsPrimaryBrush"),
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Grid.SetColumn(heading, 1);
		row.Children.Add(heading);
		Grid.SetColumn(badge, 2);
		row.Children.Add(badge);
		return row;
	}

	private Control SourceBadge(string source)
	{
		string key = source.ToLowerInvariant() switch
		{
			"builtin" => "skills.source.builtin",
			"market" => "skills.source.market",
			"url" => "skills.source.url",
			_ => "skills.source.custom",
		};
		string brush = source.Equals("builtin", StringComparison.OrdinalIgnoreCase) ? "SettingsAccentBrush" : "SettingsSecondaryBrush";
		return Pill(NativeSettingsResources.Get(key), brush);
	}

	private Control IconBadge(string name)
	{
		bool brand = name is "steam" or "github" or "qq" or "bilibili";
		return new Border
		{
			Width = 32,
			Height = 32,
			CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
			Background = Brush("SettingsSelectionBrush"),
			Child = brand
				? LineIcon.Filled(name, Brush("SettingsAccentBrush"), 16)
				: LineIcon.Build(name, Brush("SettingsAccentBrush"), 16),
		};
	}

	private Border Pill(string text, string brushKey) => new()
	{
		Background = Brush("SettingsSelectionBrush"),
		CornerRadius = new CornerRadius(NoriMetrics.RadiusPill),
		Padding = new Thickness(8, 2),
		VerticalAlignment = VerticalAlignment.Center,
		Child = new TextBlock {Text = text, FontSize = NoriMetrics.FontXs, Foreground = Brush(brushKey)},
	};

	private WrapPanel TagRow(IReadOnlyList<string> tags)
	{
		WrapPanel row = new();
		foreach (string tag in tags)
		{
			Border pill = Pill(tag, "SettingsSecondaryBrush");
			pill.Margin = new Thickness(0, 0, 6, 4);
			row.Children.Add(pill);
		}
		return row;
	}

	private Ellipse StatusDot(string brushKey) => new()
	{
		Width = 8,
		Height = 8,
		Fill = Brush(brushKey),
		VerticalAlignment = VerticalAlignment.Center,
		Margin = new Thickness(0, 0, 6, 0),
	};

	private static ToggleButton Choice(string text)
	{
		ToggleButton button = new() {Content = text, MinHeight = 32};
		button.Classes.Add("settings-choice");
		return button;
	}
}
