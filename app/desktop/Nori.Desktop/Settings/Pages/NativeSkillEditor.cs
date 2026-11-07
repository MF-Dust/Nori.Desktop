using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Settings.Pages;

/// <summary>技能专用编辑器。分类是固定下拉，工具来自当前快照。</summary>
internal static class NativeSkillEditor
{
	private static readonly string[] Categories = ["productivity", "coding", "life", "roleplay", "entertainment"];
	private static readonly string[] Icons = ["sparkles", "code", "terminal", "info", "server", "noriOS", "tool", "package", "bot", "memory"];

	/// <summary>编辑自定义、市场或远程技能。内置技能只通过复制进入这里。</summary>
	public static async Task ShowAsync(Window owner, SkillsSettingsViewModel viewModel, SkillDraft draft)
	{
		StackPanel fields = new() {Spacing = 12, Margin = new Thickness(24)};
		TextBox name = Input(draft.Name);
		TextBox description = Input(draft.Description, multiline: true);
		TextBox author = Input(draft.Author);
		TextBox version = Input(draft.Version);
		TextBox tags = Input(string.Join(", ", draft.Tags));
		TextBox instructions = Input(draft.Instructions, multiline: true);
		instructions.MinHeight = 120;
		instructions.MaxLines = 16;
		ToggleSwitch enabled = new() {Content = NativeSettingsResources.Get("common.enable"), IsChecked = draft.Enabled};
		string[] categories = Categories.Contains(draft.Category, StringComparer.OrdinalIgnoreCase)
			? Categories
			: [draft.Category, .. Categories];
		ComboBox category = new()
		{
			Name = "SkillEditorCategory",
			ItemsSource = categories.Select(CategoryLabel).ToArray(),
			SelectedIndex = Math.Max(0, Array.FindIndex(categories, item => string.Equals(item, draft.Category, StringComparison.OrdinalIgnoreCase))),
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		string[] icons = Icons.Contains(draft.Icon, StringComparer.Ordinal) ? Icons : [draft.Icon, .. Icons];
		ComboBox icon = new()
		{
			ItemsSource = icons,
			SelectedIndex = Math.Max(0, Array.IndexOf(icons, draft.Icon)),
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		HashSet<string> selected = new(draft.Tools, StringComparer.Ordinal);
		StackPanel tools = new() {Spacing = 6};
		foreach (SkillToolChoice choice in viewModel.ToolChoices(draft.Tools))
		{
			CheckBox box = new()
			{
				Content = $"{choice.Name} · {Availability(choice.Availability)}",
				IsChecked = selected.Contains(choice.Name),
				Tag = choice.Name,
			};
			tools.Children.Add(box);
		}
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.name"), name));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.description"), description));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.author"), author));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.version"), version));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.icon"), icon));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.categoryField"), category));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.tags"), tags));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.instructions"), instructions));
		fields.Children.Add(Field(NativeSettingsResources.Get("skills.tools"), new ScrollViewer
		{
			Content = tools,
			MaxHeight = 180,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
		}));
		fields.Children.Add(enabled);
		TextBlock feedback = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		StackPanel body = new() {Spacing = 12};
		body.Children.Add(new ScrollViewer
		{
			Content = fields,
			MaxHeight = Math.Max(180, Math.Min(560, owner.Bounds.Height - 180)),
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
		});
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel")};
		Button save = new() {Content = NativeSettingsResources.Get("common.save")};
		save.Classes.Add("accent");
		actions.Children.Add(cancel);
		actions.Children.Add(save);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(NativeSettingsResources.Get("common.edit"), body, Math.Min(680, Math.Max(460, owner.Bounds.Width - 48)));
		dialog.MaxHeight = Math.Max(360, owner.Bounds.Height - 24);
		bool busy = false;
		cancel.Click += (_, _) => dialog.Close();
		save.Click += async (_, _) =>
		{
			if (busy) return;
			busy = true;
			save.IsEnabled = false;
			feedback.IsVisible = true;
			feedback.Text = NativeSettingsResources.Get("common.working");
			feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsSecondaryBrush");
			try
			{
				List<string> chosen = [];
				foreach (CheckBox box in tools.Children.OfType<CheckBox>())
				{
					if (box.IsChecked == true && box.Tag is string toolName && toolName.Length > 0) chosen.Add(toolName);
				}
				SkillDraft updated = draft with
				{
					Name = (name.Text ?? "").Trim(),
					Description = (description.Text ?? "").Trim(),
					Author = (author.Text ?? "").Trim(),
					Version = (version.Text ?? "").Trim(),
					Icon = icon.SelectedIndex >= 0 ? icons[icon.SelectedIndex] : draft.Icon,
					Category = category.SelectedIndex >= 0 ? categories[category.SelectedIndex] : draft.Category,
					Tags = Split(tags.Text ?? ""),
					Instructions = instructions.Text ?? "",
					Tools = chosen,
					Enabled = enabled.IsChecked == true,
				};
				await viewModel.SaveCustomAsync(updated).ConfigureAwait(true);
				dialog.Close();
			}
			catch (Exception exception)
			{
				feedback.Text = SettingsErrorText.Resolve(exception);
				feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsErrorBrush");
				busy = false;
				save.IsEnabled = true;
			}
		};
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	private static string CategoryLabel(string category) => category switch
	{
		"productivity" => NativeSettingsResources.Get("skills.category.productivity"),
		"coding" => NativeSettingsResources.Get("skills.category.coding"),
		"life" => NativeSettingsResources.Get("skills.category.life"),
		"roleplay" => NativeSettingsResources.Get("skills.category.roleplay"),
		"entertainment" => NativeSettingsResources.Get("skills.category.entertainment"),
		_ => category,
	};

	private static string Availability(string availability) => availability switch
	{
		"disabled" => NativeSettingsResources.Get("skills.toolDisabled"),
		"missing" => NativeSettingsResources.Get("skills.toolMissing"),
		_ => NativeSettingsResources.Get("skills.toolAvailable"),
	};

	private static IReadOnlyList<string> Split(string value) =>
		value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(item => item.Length > 0).ToArray();

	private static TextBox Input(string value, bool multiline = false) => new()
	{
		Text = value,
		AcceptsReturn = multiline,
		TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
		MinHeight = multiline ? 72 : 36,
		MaxLines = multiline ? 6 : 1,
	};

	private static Control Field(string label, Control control)
	{
		StackPanel field = new() {Spacing = 6};
		field.Children.Add(new TextBlock {Text = label, FontWeight = FontWeight.SemiBold});
		field.Children.Add(control);
		return field;
	}
}
