using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nori.Desktop.Settings.Pages;

/// <summary>复杂列表型设置页的原生控件呈现器。</summary>
public sealed class NativeSettingsPagePresenter : ContentControl, IDisposable
{
	private NativeSettingsPageBase? _page;
	private SettingsPageViewModelBase? _viewModel;
	private StackPanel? _root;
	private bool _building;

	/// <summary>创建复杂设置页呈现器。</summary>
	public NativeSettingsPagePresenter()
	{
		DataContextChanged += OnDataContextChanged;
		SettingsLocalization.Changed += OnLanguageChanged;
		AttachedToVisualTree += (_, _) => Build();
	}

	private void OnLanguageChanged() => Dispatcher.UIThread.Post(Build);

	private void OnDataContextChanged(object? sender, EventArgs args)
	{
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_viewModel.Changed -= OnViewModelChanged;
		}
		_page = DataContext as NativeSettingsPageBase;
		_viewModel = _page?.ComplexViewModel;
		if (_viewModel is null)
		{
			Content = null;
			return;
		}
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;
		_viewModel.Changed += OnViewModelChanged;
		Build();
	}

	private void OnViewModelChanged() => Dispatcher.UIThread.Post(Build);

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is nameof(SettingsPageViewModelBase.IsBusy) or nameof(SettingsPageViewModelBase.ErrorMessage))
			Dispatcher.UIThread.Post(Build);
	}

	private void Build()
	{
		if (_viewModel is null || _building) return;
		_building = true;
		try
		{
			StackPanel root = new() {Spacing = 10, Margin = new Thickness(2, 0, 14, 20)};
			_root = root;
			if (_viewModel.IsBusy)
				root.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("common.working"), Foreground = Brush("SettingsSecondaryBrush")});
			if (!string.IsNullOrWhiteSpace(_viewModel.ErrorMessage))
				root.Children.Add(new TextBlock {Text = _viewModel.ErrorMessage, Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
			switch (_viewModel)
			{
				case SkillsSettingsViewModel skills:
					BuildSkills(root, skills);
					break;
				case McpSettingsViewModel mcp:
					BuildMcp(root, mcp);
					break;
				case AutomationSettingsViewModel automation:
					BuildAutomation(root, automation);
					break;
				case PluginsSettingsViewModel plugins:
					BuildPlugins(root, plugins);
					break;
				case DebugSettingsViewModel debug:
					BuildDebug(root, debug);
					break;
			}
			Content = root;
		}
		finally
		{
			_building = false;
		}
	}

	private void BuildSkills(StackPanel root, SkillsSettingsViewModel viewModel)
	{
		Grid toolbar = new() {ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8};
		Button installed = Button(NativeSettingsResources.Get("skills.installed"), () =>
		{
			viewModel.ShowMarketplace = false;
			Build();
		});
		Button marketplace = Button(NativeSettingsResources.Get("skills.marketplace"), () =>
		{
			viewModel.ShowMarketplace = true;
			Build();
		});
		installed.Classes.Set("accent", !viewModel.ShowMarketplace);
		marketplace.Classes.Set("accent", viewModel.ShowMarketplace);
		toolbar.Children.Add(installed);
		Grid.SetColumn(marketplace, 1);
		toolbar.Children.Add(marketplace);
		TextBox search = new() {Text = viewModel.SearchText, PlaceholderText = NativeSettingsResources.Get("skills.search")};
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? string.Empty;
		Grid.SetColumn(search, 2);
		toolbar.Children.Add(search);
		ComboBox category = new()
		{
			ItemsSource = SkillsSettingsViewModel.CategoryKeys.Select(CategoryName).ToArray(),
			SelectedIndex = Array.IndexOf(SkillsSettingsViewModel.CategoryKeys.ToArray(), viewModel.SelectedCategory),
			MinWidth = 120,
		};
		category.SelectionChanged += (_, _) =>
		{
			if (category.SelectedIndex >= 0) viewModel.SelectedCategory = SkillsSettingsViewModel.CategoryKeys[category.SelectedIndex];
		};
		Grid.SetColumn(category, 3);
		toolbar.Children.Add(category);
		Button newSkill = Button(NativeSettingsResources.Get("skills.new"), () => _ = RunAsync(() => NewSkillAsync(viewModel)), accent: true);
		Grid.SetColumn(newSkill, 4);
		toolbar.Children.Add(newSkill);
		root.Children.Add(toolbar);
		Button installUrl = Button(NativeSettingsResources.Get("skills.installUrl"), () => _ = RunAsync(() => InstallSkillUrlAsync(viewModel)));
		root.Children.Add(installUrl);

		IReadOnlyList<SkillItem> items = viewModel.ShowMarketplace ? viewModel.FilteredMarketplace : viewModel.FilteredInstalled;
		if (items.Count == 0)
		{
			root.Children.Add(Empty(NativeSettingsResources.Get("skills.noItems")));
			return;
		}
		foreach (SkillItem skill in items) root.Children.Add(SkillCard(viewModel, skill));
	}

	private Control SkillCard(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		StackPanel body = CardBody(skill.Name, $"{skill.Id} · {skill.Version} · {skill.Source}");
		if (!string.IsNullOrWhiteSpace(skill.Description)) body.Children.Add(new TextBlock {Text = skill.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")});
		if (skill.Tags.Count > 0) body.Children.Add(new TextBlock {Text = string.Join("  ·  ", skill.Tags), Foreground = Brush("SettingsSecondaryBrush")});
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)};
		if (viewModel.ShowMarketplace)
		{
			Button install = Button(NativeSettingsResources.Get("common.install"), () => _ = RunAsync(() => viewModel.InstallMarketplaceAsync(skill)), accent: true);
			install.IsEnabled = !viewModel.InstalledIds.Contains(skill.Id) && !viewModel.IsBusy;
			actions.Children.Add(install);
		}
		else
		{
			ToggleSwitch enabled = new() {IsChecked = skill.Enabled, IsEnabled = !viewModel.IsBusy, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
			enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleAsync(skill, enabled.IsChecked == true));
			actions.Children.Add(enabled);
		}
		Button details = Button(NativeSettingsResources.Get("skills.details"), () => _ = RunAsync(() => ShowSkillDetailsAsync(viewModel, skill)));
		actions.Children.Add(details);
		if (!viewModel.ShowMarketplace && !string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase))
		{
			Button edit = Button(NativeSettingsResources.Get("common.edit"), () => _ = RunAsync(() => EditSkillAsync(viewModel, skill)));
			actions.Children.Add(edit);
			Button uninstall = Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallSkillAsync(viewModel, skill)), danger: true);
			actions.Children.Add(uninstall);
		}
		body.Children.Add(actions);
		return WrapCard(body);
	}

	private async Task NewSkillAsync(SkillsSettingsViewModel viewModel)
	{
		SkillDraft draft = SkillsSettingsViewModel.NewDraft(Environment.UserName);
		await EditSkillFormAsync(viewModel, draft).ConfigureAwait(true);
	}

	private async Task EditSkillAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		SkillItem detailed = await viewModel.LoadDetailsAsync(skill).ConfigureAwait(true);
		await EditSkillFormAsync(viewModel, SkillsSettingsViewModel.ToDraft(detailed)).ConfigureAwait(true);
	}

	private async Task EditSkillFormAsync(SkillsSettingsViewModel viewModel, SkillDraft draft)
	{
		IReadOnlyDictionary<string, string>? form = await NativeSettingsDialogs.FormAsync(
			Owner(),
			NativeSettingsResources.Get("skills.new"),
			[
				new("name", NativeSettingsResources.Get("skills.name"), draft.Name),
				new("description", NativeSettingsResources.Get("skills.description"), draft.Description),
				new("author", NativeSettingsResources.Get("skills.author"), draft.Author),
				new("version", NativeSettingsResources.Get("skills.version"), draft.Version),
				new("icon", NativeSettingsResources.Get("skills.icon"), draft.Icon),
				new("category", NativeSettingsResources.Get("skills.categoryField"), draft.Category),
				new("tags", NativeSettingsResources.Get("skills.tags"), string.Join(", ", draft.Tags)),
				new("tools", NativeSettingsResources.Get("skills.tools"), string.Join(", ", draft.Tools)),
				new("instructions", NativeSettingsResources.Get("skills.instructions"), draft.Instructions, false, true),
			],
			NativeSettingsResources.Get("common.save")).ConfigureAwait(true);
		if (form is null) return;
		SkillDraft updated = draft with
		{
			Name = form["name"].Trim(),
			Description = form["description"].Trim(),
			Author = form["author"].Trim(),
			Version = form["version"].Trim(),
			Icon = form["icon"].Trim(),
			Category = form["category"].Trim(),
			Tags = SplitCsv(form["tags"]),
			Tools = SplitCsv(form["tools"]),
			Instructions = form["instructions"],
		};
		await viewModel.SaveCustomAsync(updated).ConfigureAwait(true);
	}

	private async Task InstallSkillUrlAsync(SkillsSettingsViewModel viewModel)
	{
		string? url = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("skills.installUrl"), NativeSettingsResources.Get("skills.url")).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(url)) await viewModel.InstallUrlAsync(url).ConfigureAwait(true);
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

	private void BuildMcp(StackPanel root, McpSettingsViewModel viewModel)
	{
		Grid toolbar = new() {ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8};
		Button servers = Button(NativeSettingsResources.Get("mcp.servers"), () => { viewModel.ShowTools = false; Build(); });
		Button tools = Button(NativeSettingsResources.Get("mcp.tools"), () => { viewModel.ShowTools = true; Build(); });
		servers.Classes.Set("accent", !viewModel.ShowTools);
		tools.Classes.Set("accent", viewModel.ShowTools);
		toolbar.Children.Add(servers);
		Grid.SetColumn(tools, 1);
		toolbar.Children.Add(tools);
		TextBox search = new() {Text = viewModel.SearchText, PlaceholderText = NativeSettingsResources.Get("mcp.search")};
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? string.Empty;
		Grid.SetColumn(search, 2);
		toolbar.Children.Add(search);
		Button import = Button(NativeSettingsResources.Get("mcp.import"), () => _ = RunAsync(() => ImportMcpAsync(viewModel)));
		Grid.SetColumn(import, 3);
		toolbar.Children.Add(import);
		Button add = Button(NativeSettingsResources.Get("mcp.add"), () => _ = RunAsync(() => EditMcpAsync(viewModel, null)), accent: true);
		Grid.SetColumn(add, 4);
		toolbar.Children.Add(add);
		root.Children.Add(toolbar);
		if (viewModel.ShowTools)
		{
			if (viewModel.FilteredTools.Count == 0) root.Children.Add(Empty(NativeSettingsResources.Get("mcp.noItems")));
			else foreach (McpToolItem tool in viewModel.FilteredTools) root.Children.Add(ToolCard(viewModel, tool));
		}
		else
		{
			if (viewModel.FilteredServers.Count == 0) root.Children.Add(Empty(NativeSettingsResources.Get("mcp.noItems")));
			else foreach (McpServerItem server in viewModel.FilteredServers) root.Children.Add(ServerCard(viewModel, server));
		}
	}

	private Control ServerCard(McpSettingsViewModel viewModel, McpServerItem server)
	{
		StackPanel body = CardBody(server.Name, $"{server.Id} · {StatusText(server.Status)}");
		if (!string.IsNullOrWhiteSpace(server.ErrorMessage)) body.Children.Add(new TextBlock {Text = server.ErrorMessage, Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
		if (server.Tools.Count > 0) body.Children.Add(new TextBlock {Text = $"{server.Tools.Count} {NativeSettingsResources.Get("mcp.tools")}: {string.Join(", ", server.Tools.Select(tool => tool.Name))}", Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)};
		Button connect = Button(server.Status.Equals("connected", StringComparison.OrdinalIgnoreCase) ? NativeSettingsResources.Get("common.stop") : NativeSettingsResources.Get("common.start"), () => _ = RunAsync(() => server.Status.Equals("connected", StringComparison.OrdinalIgnoreCase) ? viewModel.DisconnectAsync(server) : viewModel.ConnectAsync(server)));
		actions.Children.Add(connect);
		if (server.Tools.Count > 0) actions.Children.Add(Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => ExecuteMcpToolAsync(viewModel, server.Tools[0]))));
		actions.Children.Add(Button(NativeSettingsResources.Get("common.edit"), () => _ = RunAsync(() => EditMcpAsync(viewModel, server))));
		actions.Children.Add(Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => DeleteMcpAsync(viewModel, server)), danger: true));
		body.Children.Add(actions);
		return WrapCard(body);
	}

	private Control ToolCard(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		StackPanel body = CardBody(tool.Name, $"{tool.Category} · {tool.PermissionLevel}");
		body.Children.Add(new TextBlock {Text = tool.Description, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)};
		ToggleSwitch enabled = new() {IsChecked = tool.Enabled, IsEnabled = !viewModel.IsBusy, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleToolAsync(tool, enabled.IsChecked == true));
		actions.Children.Add(enabled);
		actions.Children.Add(Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => ExecuteMcpToolAsync(viewModel, tool)), enabled: tool.Enabled));
		body.Children.Add(actions);
		return WrapCard(body);
	}

	private async Task EditMcpAsync(McpSettingsViewModel viewModel, McpServerItem? existing)
	{
		McpServerDraft draft = existing is null ? McpSettingsViewModel.NewDraft(NativeSettingsResources.Get("mcp.add")) : new(
			existing.Id,
			existing.Name,
			"stdio",
			"npx",
			[],
			new Dictionary<string, string>(StringComparer.Ordinal),
			null,
			true,
			true);
		IReadOnlyDictionary<string, string>? form = await NativeSettingsDialogs.FormAsync(
			Owner(),
			NativeSettingsResources.Get("mcp.add"),
			[
				new("id", NativeSettingsResources.Get("mcp.id"), draft.Id),
				new("name", NativeSettingsResources.Get("mcp.name"), draft.Name),
				new("transport", NativeSettingsResources.Get("mcp.transport"), draft.Transport),
				new("command", NativeSettingsResources.Get("mcp.command"), draft.Command),
				new("args", NativeSettingsResources.Get("mcp.args"), string.Join(" ", draft.Arguments)),
				new("env", NativeSettingsResources.Get("mcp.env"), FormatEnv(draft.Environment), false, true),
				new("url", NativeSettingsResources.Get("mcp.sseUrl"), draft.Url ?? ""),
			],
			NativeSettingsResources.Get("common.save")).ConfigureAwait(true);
		if (form is null) return;
		McpServerDraft updated = draft with
		{
			Id = form["id"].Trim(),
			Name = form["name"].Trim(),
			Transport = form["transport"].Trim().ToLowerInvariant(),
			Command = form["command"].Trim(),
			Arguments = SplitArgs(form["args"]),
			Environment = ParseEnv(form["env"]),
			Url = string.IsNullOrWhiteSpace(form["url"]) ? null : form["url"].Trim(),
		};
		McpServerItem? test = await viewModel.TestServerAsync(updated).ConfigureAwait(true);
		if (test is not null && test.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
			&& !await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.test"), test.ErrorMessage ?? NativeSettingsResources.Get("mcp.disconnected")).ConfigureAwait(true)) return;
		await viewModel.SaveServerAsync(updated).ConfigureAwait(true);
	}

	private async Task ImportMcpAsync(McpSettingsViewModel viewModel)
	{
		string? url = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("mcp.import"), NativeSettingsResources.Get("mcp.url")).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(url)) await viewModel.ImportAsync(url).ConfigureAwait(true);
	}

	private async Task DeleteMcpAsync(McpSettingsViewModel viewModel, McpServerItem server)
	{
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("mcp.deleteConfirm"), true).ConfigureAwait(true))
			await viewModel.DeleteAsync(server).ConfigureAwait(true);
	}

	private async Task ExecuteMcpToolAsync(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		string? json = await NativeSettingsDialogs.PromptAsync(Owner(), tool.Name, NativeSettingsResources.Get("mcp.executeArgs"), "{}", multiline: true).ConfigureAwait(true);
		if (json is null) return;
		JsonElement result = await viewModel.ExecuteToolAsync(tool, json).ConfigureAwait(true);
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), tool.Name, result.ValueKind == JsonValueKind.Undefined ? NativeSettingsResources.Get("common.none") : result.GetRawText()).ConfigureAwait(true);
	}

	private void BuildAutomation(StackPanel root, AutomationSettingsViewModel viewModel)
	{
		AutomationStateModel state = viewModel.State;
		StackPanel settings = CardBody(NativeSettingsResources.Get("automation.enabled"), state.UnavailableReason);
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.enabled"), state.Enabled, value => viewModel.UpdateSettingsAsync(enabled: value)));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.pointer"), state.AllowPointer, value => viewModel.UpdateSettingsAsync(allowPointer: value), state.Available));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.keyboard"), state.AllowKeyboard, value => viewModel.UpdateSettingsAsync(allowKeyboard: value), state.Available));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.scroll"), state.AllowScroll, value => viewModel.UpdateSettingsAsync(allowScroll: value), state.Available));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.browser"), state.BrowserEnabled, value => viewModel.UpdateSettingsAsync(browserEnabled: value), state.Browser.Available));
		root.Children.Add(WrapCard(settings));

		StackPanel capability = CardBody(NativeSettingsResources.Get("automation.capabilities"), NativeSettingsResources.Get("automation.status"));
		foreach (AutomationCapabilityItem item in state.Capabilities)
			capability.Children.Add(new TextBlock {Text = $"{(item.Available ? "✓" : "×")}  {item.Name}{(item.Available ? "" : $": {item.UnavailableReason}")}", Foreground = item.Available ? Brush("SettingsPrimaryBrush") : Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel capabilityActions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)};
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.probe"), () => _ = RunAsync(async () =>
		{
			(bool available, string? reason) = await viewModel.ProbeVisionAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("automation.probe"), available ? NativeSettingsResources.Get("common.enable") : reason ?? NativeSettingsResources.Get("common.none")).ConfigureAwait(true);
		})));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserStart"), () => _ = RunAsync(() => viewModel.StartBrowserAsync())));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserStop"), () => _ = RunAsync(() => viewModel.StopBrowserAsync())));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserTask"), () => _ = RunAsync(() => StartBrowserTaskAsync(viewModel)), enabled: state.Browser.Available));
		capability.Children.Add(capabilityActions);
		root.Children.Add(WrapCard(capability));

		if (state.PendingApprovals.Count > 0)
		{
			StackPanel approvals = CardBody(NativeSettingsResources.Get("automation.approvals"), null);
			foreach (AutomationApprovalItem approval in state.PendingApprovals)
			{
				Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 4)};
				row.Children.Add(new TextBlock {Text = $"{approval.RequestId} · {approval.TaskId} · {string.Join(", ", approval.ActionKinds)}", Foreground = Brush("SettingsPrimaryBrush"), TextWrapping = TextWrapping.Wrap});
				StackPanel response = new() {Orientation = Orientation.Horizontal, Spacing = 6};
				response.Children.Add(Button(NativeSettingsResources.Get("automation.approve"), () => _ = RunAsync(() => viewModel.RespondApprovalAsync(approval.RequestId, true))));
				response.Children.Add(Button(NativeSettingsResources.Get("automation.deny"), () => _ = RunAsync(() => viewModel.RespondApprovalAsync(approval.RequestId, false)), danger: true));
				Grid.SetColumn(response, 1);
				row.Children.Add(response);
				approvals.Children.Add(row);
			}
			root.Children.Add(WrapCard(approvals));
		}

		StackPanel tasks = CardBody(NativeSettingsResources.Get("automation.status"), $"{state.QueuedCount} {NativeSettingsResources.Get("automation.queued")}");
		if (state.Tasks.Count == 0 && state.ActiveTask is null) tasks.Children.Add(Empty(NativeSettingsResources.Get("automation.noTasks")));
		foreach (AutomationTaskItem task in state.Tasks) tasks.Children.Add(TaskRow(viewModel, task));
		if (state.ActiveTask is { } active && !state.Tasks.Any(item => item.Id == active.Id)) tasks.Children.Add(TaskRow(viewModel, active));
		tasks.Children.Add(Button(NativeSettingsResources.Get("automation.stopAll"), () => _ = RunAsync(() => viewModel.StopAllAsync()), danger: true));
		root.Children.Add(WrapCard(tasks));

		StackPanel audit = CardBody(NativeSettingsResources.Get("automation.audit"), null);
		audit.Children.Add(Button(NativeSettingsResources.Get("automation.refreshAudit"), () => _ = RunAsync(() => viewModel.LoadAuditAsync())));
		if (viewModel.Audit.Count == 0) audit.Children.Add(Empty(NativeSettingsResources.Get("automation.noAudit")));
		foreach (AutomationAuditItem item in viewModel.Audit)
			audit.Children.Add(new TextBlock {Text = $"{item.Time} · {item.TaskKind} · {item.Category} · {item.Outcome}{(item.FailureCode is null ? "" : $" · {item.FailureCode}")}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")});
		root.Children.Add(WrapCard(audit));
	}

	private Control ToggleRow(AutomationSettingsViewModel viewModel, string label, bool value, Func<bool, Task> changed, bool enabled = true)
	{
		ToggleSwitch toggle = new() {IsChecked = value, IsEnabled = enabled && !viewModel.IsBusy, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		toggle.IsCheckedChanged += (_, _) => _ = RunAsync(() => changed(toggle.IsChecked == true));
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4)};
		row.Children.Add(new TextBlock {Text = label, Foreground = Brush("SettingsPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center});
		Grid.SetColumn(toggle, 1);
		row.Children.Add(toggle);
		return row;
	}

	private Control TaskRow(AutomationSettingsViewModel viewModel, AutomationTaskItem task)
	{
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 4)};
		StackPanel details = new() {Spacing = 2};
		details.Children.Add(new TextBlock {Text = $"{task.TaskKind} · {task.State}", Foreground = Brush("SettingsPrimaryBrush")});
		details.Children.Add(new TextBlock {Text = $"{task.Id} · {task.ProgressCategory}{(task.ResultSummary is null ? "" : $" · {task.ResultSummary}")}", Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		row.Children.Add(details);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 6};
		if (task.ApprovalRequestId is {Length: > 0} requestId)
		{
			actions.Children.Add(Button(NativeSettingsResources.Get("automation.approve"), () => _ = RunAsync(() => viewModel.RespondApprovalAsync(requestId, true))));
			actions.Children.Add(Button(NativeSettingsResources.Get("automation.deny"), () => _ = RunAsync(() => viewModel.RespondApprovalAsync(requestId, false)), danger: true));
		}
		actions.Children.Add(Button(NativeSettingsResources.Get("automation.stopTask"), () => _ = RunAsync(() => viewModel.StopTaskAsync(task.Id)), danger: true));
		Grid.SetColumn(actions, 1);
		row.Children.Add(actions);
		return row;
	}

	private async Task StartBrowserTaskAsync(AutomationSettingsViewModel viewModel)
	{
		string? actions = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("automation.browserTask"), NativeSettingsResources.Get("automation.taskActions"), "[{\"type\":\"wait\",\"milliseconds\":1000}]", multiline: true).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(actions)) await viewModel.StartBrowserTaskAsync(actions).ConfigureAwait(true);
	}

	private void BuildPlugins(StackPanel root, PluginsSettingsViewModel viewModel)
	{
		StackPanel trust = CardBody(NativeSettingsResources.Get("plugins.confirmTrust"), NativeSettingsResources.Get("plugins.trust"));
		Button confirm = Button(viewModel.TrustConfirmed ? NativeSettingsResources.Get("common.enable") : NativeSettingsResources.Get("plugins.confirmTrust"), () => _ = RunAsync(async () =>
		{
			if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("plugins.confirmTrust"), NativeSettingsResources.Get("plugins.trust")).ConfigureAwait(true)) await viewModel.ConfirmTrustAsync().ConfigureAwait(true);
		}), accent: true, enabled: !viewModel.TrustConfirmed && !viewModel.SafeMode);
		trust.Children.Add(confirm);
		if (viewModel.SafeMode) trust.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("plugins.safeMode"), Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
		root.Children.Add(WrapCard(trust));
		Button install = Button(NativeSettingsResources.Get("plugins.install"), () => _ = RunAsync(() => viewModel.InstallLocalAsync()), accent: true, enabled: viewModel.CanInstall && !viewModel.IsBusy);
		root.Children.Add(install);
		if (viewModel.Plugins.Count == 0)
		{
			root.Children.Add(Empty(NativeSettingsResources.Get("plugins.noItems")));
			return;
		}
		foreach (PluginItem plugin in viewModel.Plugins) root.Children.Add(PluginCard(viewModel, plugin));
	}

	private Control PluginCard(PluginsSettingsViewModel viewModel, PluginItem plugin)
	{
		StackPanel body = CardBody(plugin.Name, $"{plugin.Id} · {plugin.Version} · {plugin.State}");
		if (!string.IsNullOrWhiteSpace(plugin.Description)) body.Children.Add(new TextBlock {Text = plugin.Description, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		if (!string.IsNullOrWhiteSpace(plugin.ErrorMessage)) body.Children.Add(new TextBlock {Text = plugin.ErrorMessage, Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)};
		ToggleSwitch enabled = new() {IsChecked = plugin.Enabled, IsEnabled = viewModel.CanInstall && !viewModel.IsBusy, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => plugin.Enabled ? viewModel.DisableAsync(plugin) : viewModel.EnableAsync(plugin));
		actions.Children.Add(enabled);
		actions.Children.Add(Button(NativeSettingsResources.Get("common.details"), () => _ = RunAsync(() => ShowPluginDetailsAsync(plugin))));
		actions.Children.Add(Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallPluginAsync(viewModel, plugin)), danger: true));
		body.Children.Add(actions);
		if (plugin.Capabilities.Count > 0)
		{
			body.Children.Add(new TextBlock {Text = $"{NativeSettingsResources.Get("plugins.capabilities")}: {string.Join(", ", plugin.Capabilities.Select(capability => $"{capability} ({PluginsSettingsViewModel.CapabilityLabel(plugin, capability)})"))}", Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		}
		return WrapCard(body);
	}

	private async Task ShowPluginDetailsAsync(PluginItem plugin)
	{
		string text = $"{plugin.Name}\n\n{plugin.Description}\n\n{NativeSettingsResources.Get("plugins.capabilities")}: {string.Join(", ", plugin.Capabilities)}";
		if (plugin.Homepage is {Length: > 0}) text += $"\n\n{plugin.Homepage}";
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), plugin.Name, text).ConfigureAwait(true);
	}

	private async Task UninstallPluginAsync(PluginsSettingsViewModel viewModel, PluginItem plugin)
	{
		if (!await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("plugins.uninstallConfirm"), true).ConfigureAwait(true)) return;
		bool deleteData = await NativeSettingsDialogs.ConfirmAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.deleteData"), true).ConfigureAwait(true);
		PluginUninstallItem result = await viewModel.UninstallAsync(plugin, deleteData).ConfigureAwait(true);
		if (result.RequiresRestart) await NativeSettingsDialogs.ShowMessageAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.restart")).ConfigureAwait(true);
	}

	private void BuildDebug(StackPanel root, DebugSettingsViewModel viewModel)
	{
		root.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("debug.warning"), Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel diagnostic = CardBody(NativeSettingsResources.Get("debug.diagnostic"), null);
		StackPanel diagnosticActions = new() {Orientation = Orientation.Horizontal, Spacing = 8};
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.refresh"), () => _ = RunAsync(() => viewModel.RefreshDiagnosticAsync())));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyDiagnosticAsync())));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.export"), () => _ = RunAsync(() => ExportDiagnosticsAsync(viewModel))));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.openFolder"), () => _ = RunAsync(() => viewModel.OpenLogFolderAsync())));
		diagnostic.Children.Add(diagnosticActions);
		foreach ((string key, string value) in viewModel.Diagnostic) diagnostic.Children.Add(new TextBlock {Text = $"{key}: {value}", Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		root.Children.Add(WrapCard(diagnostic));

		StackPanel logs = CardBody(NativeSettingsResources.Get("debug.logs"), null);
		Grid logToolbar = new() {ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 8};
		ComboBox filter = new() {ItemsSource = new[] {NativeSettingsResources.Get("debug.all"), "error", "warn", "info"}, SelectedIndex = viewModel.LevelFilter switch {"error" => 1, "warn" => 2, "info" => 3, _ => 0}, MinWidth = 100};
		filter.SelectionChanged += (_, _) => viewModel.LevelFilter = filter.SelectedIndex switch {1 => "error", 2 => "warn", 3 => "info", _ => "all"};
		logToolbar.Children.Add(filter);
		Button refresh = Button(NativeSettingsResources.Get("debug.refresh"), () => _ = RunAsync(() => viewModel.RefreshLogsAsync()));
		Grid.SetColumn(refresh, 1);
		logToolbar.Children.Add(refresh);
		Button clear = Button(NativeSettingsResources.Get("debug.clear"), () => _ = RunAsync(async () =>
		{
			if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("debug.clear"), NativeSettingsResources.Get("debug.clearConfirm"), true).ConfigureAwait(true)) await viewModel.ClearLogsAsync().ConfigureAwait(true);
		}));
		Grid.SetColumn(clear, 2);
		logToolbar.Children.Add(clear);
		Button copy = Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyLogsAsync()));
		Grid.SetColumn(copy, 3);
		logToolbar.Children.Add(copy);
		logs.Children.Add(logToolbar);
		ScrollViewer logScroll = new() {MaxHeight = 300};
		StackPanel logItems = new() {Spacing = 3};
		foreach (DebugLogItem item in viewModel.FilteredLogs)
			logItems.Children.Add(new TextBlock {Text = $"[{item.Time}] [{item.Level}] [{item.Source}] {item.Message}", FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Foreground = item.Level.Equals("error", StringComparison.OrdinalIgnoreCase) ? Brush("SettingsErrorBrush") : Brush("SettingsSecondaryBrush")});
		if (logItems.Children.Count == 0) logItems.Children.Add(Empty(NativeSettingsResources.Get("debug.noLogs")));
		logScroll.Content = logItems;
		logs.Children.Add(logScroll);
		root.Children.Add(WrapCard(logs));

		StackPanel tools = CardBody(NativeSettingsResources.Get("debug.crash"), null);
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.gc"), () => _ = RunAsync(async () =>
		{
			long released = await viewModel.CollectGarbageAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("debug.gc"), $"{NativeSettingsResources.Get("debug.released")}: {released}").ConfigureAwait(true);
		})));
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.testLog"), () => _ = RunAsync(() => viewModel.WriteTestLogAsync())));
		bool crashEnabled = viewModel.CrashTestsAvailable;
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.uiCrash"), () => _ = RunCrashAsync(viewModel, "ui_thread", false), danger: true, enabled: crashEnabled));
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.backgroundCrash"), () => _ = RunCrashAsync(viewModel, "background_thread", true), danger: true, enabled: crashEnabled));
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.taskCrash"), () => _ = RunCrashAsync(viewModel, "unobserved_task", true), danger: true, enabled: crashEnabled));
		root.Children.Add(WrapCard(tools));
	}

	private async Task ExportDiagnosticsAsync(DebugSettingsViewModel viewModel)
	{
		DiagnosticExportItem? result = await viewModel.ExportDiagnosticsAsync().ConfigureAwait(true);
		if (result is not null) await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("debug.export"), $"{result.FileName}\n{result.Bytes} bytes").ConfigureAwait(true);
	}

	private async Task RunCrashAsync(DebugSettingsViewModel viewModel, string mode, bool mayExit)
	{
		string prompt = mayExit ? NativeSettingsResources.Get("debug.exitConfirm") : NativeSettingsResources.Get("debug.crashConfirm");
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("debug.crash"), prompt, true).ConfigureAwait(true)) await viewModel.TriggerCrashAsync(mode).ConfigureAwait(true);
	}

	private StackPanel CardBody(string title, string? subtitle)
	{
		StackPanel body = new() {Spacing = 4};
		body.Children.Add(new TextBlock {Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("SettingsPrimaryBrush")});
		if (!string.IsNullOrWhiteSpace(subtitle)) body.Children.Add(new TextBlock {Text = subtitle, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		return body;
	}

	private Border WrapCard(StackPanel body) => new()
	{
		Background = Brush("SettingsCardBrush"),
		BorderBrush = Brush("SettingsBorderBrush"),
		BorderThickness = new Thickness(1),
		CornerRadius = new CornerRadius(8),
		Padding = new Thickness(16, 12),
		Child = body,
	};

	private TextBlock Empty(string text) => new() {Text = text, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8)};

	private Button Button(string text, Action action, bool accent = false, bool danger = false, bool enabled = true)
	{
		Button button = new() {Content = text, IsEnabled = enabled, MinHeight = 32};
		if (accent) button.Classes.Add("accent");
		if (danger) button.Classes.Add("danger");
		button.Click += (_, _) => action();
		return button;
	}

	private async Task RunAsync(Func<Task> action)
	{
		try
		{
			if (_viewModel is not null) _viewModel.ClearError();
			await action().ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			_viewModel?.ReportError(exception);
		}
		finally
		{
			Dispatcher.UIThread.Post(Build);
		}
	}

	private Window Owner() => TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("设置窗口尚未就绪");

	private IBrush Brush(string key) => SettingsBrushes.Resolve(this, key);

	/// <summary>解除页面和语言资源订阅。</summary>
	public void Dispose()
	{
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_viewModel.Changed -= OnViewModelChanged;
		}
		SettingsLocalization.Changed -= OnLanguageChanged;
	}

	private static string CategoryName(string category) => category switch
	{
		"all" => NativeSettingsResources.Get("common.all"),
		"productivity" => SettingsLocalization.IsEnglish ? "Productivity" : "生产力",
		"coding" => SettingsLocalization.IsEnglish ? "Coding" : "编程",
		"life" => SettingsLocalization.IsEnglish ? "Life & learning" : "生活与学习",
		"roleplay" => SettingsLocalization.IsEnglish ? "Roleplay" : "情感与角色",
		"entertainment" => SettingsLocalization.IsEnglish ? "Entertainment" : "游戏与娱乐",
		_ => category,
	};

	private static string StatusText(string status) => status switch
	{
		"connected" => NativeSettingsResources.Get("mcp.connected"),
		_ => NativeSettingsResources.Get("mcp.disconnected"),
	};

	private static IReadOnlyList<string> SplitCsv(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(item => item.Length > 0).ToArray();

	private static IReadOnlyList<string> SplitArgs(string value) => value.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();

	private static Dictionary<string, string> ParseEnv(string value)
	{
		Dictionary<string, string> result = new(StringComparer.Ordinal);
		foreach (string line in value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int index = line.IndexOf('=');
			if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
		}
		return result;
	}

	private static string FormatEnv(IReadOnlyDictionary<string, string> values) => string.Join(Environment.NewLine, values.Select(item => $"{item.Key}={item.Value}"));
}
