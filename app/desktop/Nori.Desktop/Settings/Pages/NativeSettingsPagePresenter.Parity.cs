using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Settings.Pages;

/// <summary>技能、MCP、自动化与插件菜单，保留筛选控件和稳定滚动外壳。</summary>
public sealed partial class NativeSettingsPagePresenter
{
	private StackPanel? _complexBody;
	private StackPanel? _complexRoot;
	private SettingsPageViewModelBase? _complexOwner;
	private Func<string>? _complexState;
	private Action<StackPanel>? _complexRender;
	private string? _complexFingerprint;

	private void BuildAutomation(StackPanel root, AutomationSettingsViewModel viewModel) =>
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new {viewModel.State, viewModel.Audit, viewModel.IsSupported, viewModel.CanConfigure}),
			body => RenderAutomation(body, viewModel));

	private void BuildPlugins(StackPanel root, PluginsSettingsViewModel viewModel) =>
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new {viewModel.Plugins, viewModel.SafeMode, viewModel.TrustConfirmed}),
			body => RenderPlugins(body, viewModel));

	private void BuildSkills(StackPanel root, SkillsSettingsViewModel viewModel)
	{
		Button installed = Button(NativeSettingsResources.Get("skills.installed"), () => { viewModel.ShowMarketplace = false; Build(); });
		Button marketplace = Button(NativeSettingsResources.Get("skills.marketplace"), () => { viewModel.ShowMarketplace = true; Build(); });
		root.Children.Add(ParityActions(installed, marketplace,
			Button(NativeSettingsResources.Get("skills.new"), () => _ = RunAsync(() => NewSkillAsync(viewModel)), accent: true),
			Button(NativeSettingsResources.Get("skills.installUrl"), () => _ = RunAsync(() => InstallSkillUrlAsync(viewModel))),
			Button(ParityText("刷新", "Refresh"), () => _ = RunAsync(() => viewModel.RefreshAsync()))));
		Grid filters = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8};
		TextBox search = new() {Text = viewModel.SearchText, PlaceholderText = NativeSettingsResources.Get("skills.search"), MinWidth = 80};
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? string.Empty;
		filters.Children.Add(search);
		ComboBox category = new()
		{
			ItemsSource = SkillsSettingsViewModel.CategoryKeys.Select(CategoryName).ToArray(),
			SelectedIndex = Array.IndexOf(SkillsSettingsViewModel.CategoryKeys.ToArray(), viewModel.SelectedCategory),
			MinWidth = 128,
		};
		category.SelectionChanged += (_, _) =>
		{
			if (category.SelectedIndex >= 0) viewModel.SelectedCategory = SkillsSettingsViewModel.CategoryKeys[category.SelectedIndex];
		};
		Grid.SetColumn(category, 1);
		filters.Children.Add(category);
		root.Children.Add(filters);
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new {viewModel.ShowMarketplace, viewModel.SearchText, viewModel.SelectedCategory, viewModel.Installed, viewModel.Marketplace}),
			list =>
			{
				installed.Classes.Set("accent", !viewModel.ShowMarketplace);
				marketplace.Classes.Set("accent", viewModel.ShowMarketplace);
				IReadOnlyList<SkillItem> items = viewModel.ShowMarketplace ? viewModel.FilteredMarketplace : viewModel.FilteredInstalled;
				if (items.Count == 0) list.Children.Add(Empty(NativeSettingsResources.Get("skills.noItems")));
				foreach (SkillItem skill in items) list.Children.Add(SkillCard(viewModel, skill));
			});
	}

	private async Task DeleteMcpAsync(McpSettingsViewModel viewModel, McpServerItem server)
	{
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("mcp.deleteConfirm"), true).ConfigureAwait(true))
			await viewModel.DeleteAsync(server).ConfigureAwait(true);
	}

	private async Task EditMcpAsync(McpSettingsViewModel viewModel, McpServerItem? existing)
	{
		McpServerDraft draft = existing is null
			? McpSettingsViewModel.NewDraft(NativeSettingsResources.Get("mcp.add"))
			: await viewModel.LoadDraftAsync(existing).ConfigureAwait(true);
		await NativeMcpServerEditor.ShowAsync(Owner(), viewModel, draft, existing is not null, existing?.HasEnvironment == true).ConfigureAwait(true);
	}

	private async Task EditSkillAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		SkillItem detailed = await viewModel.LoadDetailsAsync(skill).ConfigureAwait(true);
		SkillDraft draft = SkillsSettingsViewModel.ToDraft(detailed);
		if (string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase))
			draft = draft with {Id = SkillsSettingsViewModel.NewDraft(detailed.Author).Id, Source = "custom", InstalledAt = 0};
		await EditSkillFormAsync(viewModel, draft).ConfigureAwait(true);
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

	private async Task ExecuteMcpToolAsync(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		string schema = tool.InputSchema.ValueKind == JsonValueKind.Object
			? JsonSerializer.Serialize(tool.InputSchema, new JsonSerializerOptions {WriteIndented = true})
			: "{}";
		string? json = await NativeSettingsDialogs.PromptAsync(
			Owner(), tool.Name,
			$"{tool.Description}\n\n{ParityText("参数结构", "Input schema")}\n{schema}\n\n{NativeSettingsResources.Get("mcp.executeArgs")}",
			McpSettingsViewModel.CreateToolArguments(tool), multiline: true).ConfigureAwait(true);
		if (json is null) return;
		JsonElement result = await viewModel.ExecuteToolAsync(tool, json).ConfigureAwait(true);
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), tool.Name, result.ValueKind == JsonValueKind.Undefined
			? NativeSettingsResources.Get("common.none")
			: JsonSerializer.Serialize(result, new JsonSerializerOptions {WriteIndented = true})).ConfigureAwait(true);
	}

	private async Task ImportMcpAsync(McpSettingsViewModel viewModel)
	{
		string? url = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("mcp.import"), NativeSettingsResources.Get("mcp.url")).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(url)) await viewModel.ImportAsync(url).ConfigureAwait(true);
	}

	private void InitializeComplexBody(StackPanel root, SettingsPageViewModelBase owner, Func<string> state, Action<StackPanel> render)
	{
		_complexRoot = root;
		_complexOwner = owner;
		_complexBody = new StackPanel {Spacing = 14};
		_complexState = state;
		_complexRender = render;
		_complexFingerprint = null;
		root.Children.Add(_complexBody);
		UpdateComplexPage();
	}

	private async Task InstallSkillUrlAsync(SkillsSettingsViewModel viewModel)
	{
		string? url = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("skills.installUrl"), NativeSettingsResources.Get("skills.url")).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(url)) await viewModel.InstallUrlAsync(url).ConfigureAwait(true);
	}

	private static object McpToolFingerprint(McpToolItem tool) => new
	{
		tool.Name, tool.Description, tool.PermissionLevel, tool.Enabled, tool.ServerId,
		Schema = tool.InputSchema.ValueKind == JsonValueKind.Object ? tool.InputSchema.GetRawText() : "",
	};

	private async Task NewSkillAsync(SkillsSettingsViewModel viewModel)
	{
		SkillDraft draft = SkillsSettingsViewModel.NewDraft(Environment.UserName);
		await EditSkillFormAsync(viewModel, draft).ConfigureAwait(true);
	}

	private static WrapPanel ParityActions(params Control[] children)
	{
		WrapPanel panel = new();
		foreach (Control child in children)
		{
			child.Margin = new Thickness(0, 0, 8, 8);
			panel.Children.Add(child);
		}
		return panel;
	}

	private static string ParityText(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;

	private static string PluginCapabilityLabel(PluginItem plugin, string capability) =>
		PluginsSettingsViewModel.CapabilityLabel(plugin, capability) switch
		{
			"granted" => ParityText("已授权且可用", "Granted and available"),
			"unavailable" => ParityText("未授权或不可用", "Not granted or unavailable"),
			_ => ParityText("未声明", "Not declared"),
		};

	private Control PluginCard(PluginsSettingsViewModel viewModel, PluginItem plugin)
	{
		StackPanel body = CardBody(plugin.Name, $"{plugin.Version} · {plugin.Author} · {PluginStateLabel(plugin.State)}");
		if (!string.IsNullOrWhiteSpace(plugin.Description)) body.Children.Add(Empty(plugin.Description));
		if (!string.IsNullOrWhiteSpace(plugin.ErrorCode) || !string.IsNullOrWhiteSpace(plugin.ErrorMessage))
			body.Children.Add(Empty(string.Join(" · ", new[] {plugin.ErrorCode, plugin.ErrorMessage}.Where(item => !string.IsNullOrWhiteSpace(item)))));
		if (plugin.RequiresRestart || plugin.State == "pending_restart") body.Children.Add(Empty(NativeSettingsResources.Get("plugins.restart")));
		WrapPanel actions = ParityActions(Button(NativeSettingsResources.Get("common.details"), () => _ = RunAsync(() => ShowPluginDetailsAsync(plugin))));
		bool ready = plugin.State is not ("loading" or "stopping" or "pending_restart");
		if (plugin.State is "active" or "failed")
			actions.Children.Add(Button(NativeSettingsResources.Get("common.disable"), () => _ = RunAsync(() => viewModel.DisableAsync(plugin)), enabled: ready));
		if (plugin.State is "installed" or "disabled" or "failed")
			actions.Children.Add(Button(plugin.State == "failed" ? ParityText("重试", "Retry") : NativeSettingsResources.Get("common.enable"),
				() => _ = RunAsync(() => viewModel.EnableAsync(plugin)), accent: true, enabled: viewModel.CanInstall && ready));
		actions.Children.Add(Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallPluginAsync(viewModel, plugin)), danger: true, enabled: ready));
		foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 8, 8);
		body.Children.Add(actions);
		foreach (string capability in plugin.Capabilities)
			body.Children.Add(Empty($"{capability} · {ParityText("必需", "Required")} · {PluginCapabilityLabel(plugin, capability)}"));
		foreach (string capability in plugin.OptionalCapabilities)
			body.Children.Add(Empty($"{capability} · {ParityText("可选", "Optional")} · {PluginCapabilityLabel(plugin, capability)}"));
		if (plugin.Capabilities.Count == 0 && plugin.OptionalCapabilities.Count == 0)
			body.Children.Add(Empty(ParityText("未声明扩展能力", "No extension capabilities declared")));
		return WrapCard(body);
	}

	private static string PluginStateLabel(string state) => state switch
	{
		"installed" => ParityText("已安装", "Installed"),
		"active" => ParityText("运行中", "Active"),
		"disabled" => ParityText("已停用", "Disabled"),
		"failed" => ParityText("运行失败", "Failed"),
		"loading" => ParityText("加载中", "Loading"),
		"stopping" => ParityText("停止中", "Stopping"),
		"pending_restart" => ParityText("等待重启", "Restart required"),
		_ => state,
	};

	private void RenderAutomation(StackPanel root, AutomationSettingsViewModel viewModel)
	{
		AutomationStateModel state = viewModel.State;
		StackPanel settings = CardBody(NativeSettingsResources.Get("automation.enabled"), state.UnavailableReason);
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.enabled"), state.Enabled, value => viewModel.UpdateFrontendTogglesAsync(enabled: value), viewModel.CanConfigure));
		settings.Children.Add(ToggleRow(viewModel, ParityText("桌面自动化", "Desktop automation"), viewModel.DesktopEnabled, value => viewModel.UpdateFrontendTogglesAsync(desktopEnabled: value), viewModel.CanConfigure && state.Enabled));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.pointer"), state.AllowPointer, value => viewModel.UpdateSettingsAsync(allowPointer: value), viewModel.CanConfigure && state.Enabled));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.keyboard"), state.AllowKeyboard, value => viewModel.UpdateSettingsAsync(allowKeyboard: value), viewModel.CanConfigure && state.Enabled));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.scroll"), state.AllowScroll, value => viewModel.UpdateSettingsAsync(allowScroll: value), viewModel.CanConfigure && state.Enabled));
		settings.Children.Add(ToggleRow(viewModel, NativeSettingsResources.Get("automation.browser"), state.BrowserEnabled, value => viewModel.UpdateFrontendTogglesAsync(browserEnabled: value), viewModel.CanConfigure && state.Enabled));
		root.Children.Add(WrapCard(settings));
		StackPanel browser = CardBody(NativeSettingsResources.Get("automation.browser"), $"{state.Browser.State} · {state.Browser.UnavailableReason}");
		browser.Children.Add(Empty(state.VisionReady ? ParityText("视觉模型已就绪", "Vision model ready") : ParityText("视觉模型尚未就绪", "Vision model not ready")));
		root.Children.Add(WrapCard(browser));

		StackPanel capability = CardBody(NativeSettingsResources.Get("automation.capabilities"), NativeSettingsResources.Get("automation.status"));
		foreach (AutomationCapabilityItem item in state.Capabilities)
			capability.Children.Add(new TextBlock {Text = $"{(item.Available ? "✓" : "×")}  {item.Name}{(item.Available ? "" : $": {item.UnavailableReason}")}", Foreground = item.Available ? Brush("SettingsPrimaryBrush") : Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		WrapPanel capabilityActions = new() {Margin = new Thickness(0, 8, 0, 0)};
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.probe"), () => _ = RunAsync(async () =>
		{
			(bool available, string? reason) = await viewModel.ProbeVisionAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("automation.probe"), available ? NativeSettingsResources.Get("common.enable") : reason ?? NativeSettingsResources.Get("common.none")).ConfigureAwait(true);
		}), enabled: viewModel.IsSupported));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserStart"), () => _ = RunAsync(() => viewModel.StartBrowserAsync()), enabled: state.Browser.Available && !state.Browser.Running));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserStop"), () => _ = RunAsync(() => viewModel.StopBrowserAsync()), enabled: state.Browser.Running));
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.browserTask"), () => _ = RunAsync(() => StartBrowserTaskAsync(viewModel)), enabled: state.Browser.Available));
		foreach (Control action in capabilityActions.Children) action.Margin = new Thickness(0, 0, 8, 8);
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
		StackPanel security = CardBody(ParityText("安全与控制", "Safety and control"), null);
		security.Children.Add(Empty(ParityText(
			"自动化默认关闭，桌面和浏览器权限需要分别开启。涉及敏感操作时，任务会等待你的确认。",
			"Automation is off by default. Desktop and browser permissions are enabled separately. Sensitive actions wait for your approval.")));
		security.Children.Add(Empty(ParityText(
			"关闭总开关或撤回输入权限会停止正在运行的任务。审计记录只保留脱敏状态，不保存截图、页面正文或输入内容。",
			"Turning automation off or revoking input permissions stops active tasks. Audit records contain redacted status without screenshots, page text, or input content.")));
		root.Children.Add(WrapCard(security));
	}

	private async Task ShowPluginDetailsAsync(PluginItem plugin)
	{
		string text = string.Join(Environment.NewLine + Environment.NewLine, new[]
		{
			plugin.Description,
			$"ID: {plugin.Id}",
			$"{NativeSettingsResources.Get("skills.version")}: {plugin.Version}",
			$"{NativeSettingsResources.Get("skills.author")}: {plugin.Author}",
			$"{ParityText("许可证", "License")}: {plugin.License ?? "—"}",
			$"{ParityText("主页", "Homepage")}: {plugin.Homepage ?? "—"}",
			$"{ParityText("代码仓库", "Repository")}: {plugin.Repository ?? "—"}",
			$"{NativeSettingsResources.Get("plugins.capabilities")}: {string.Join(", ", plugin.Capabilities.Concat(plugin.OptionalCapabilities))}",
			$"{PluginStateLabel(plugin.State)} · {plugin.ErrorCode} · {plugin.ErrorMessage}",
		});
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), plugin.Name, text).ConfigureAwait(true);
	}

	private async Task ShowSkillDetailsAsync(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		SkillItem detailed = await viewModel.LoadDetailsAsync(skill).ConfigureAwait(true);
		string text = string.IsNullOrWhiteSpace(detailed.Instructions) ? NativeSettingsResources.Get("common.none") : detailed.Instructions;
		if (detailed.Tools.Count > 0) text += Environment.NewLine + Environment.NewLine + $"{NativeSettingsResources.Get("skills.tools")}: {string.Join(", ", detailed.Tools)}";
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), detailed.Name, text).ConfigureAwait(true);
	}

	private Control SkillCard(SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		StackPanel body = CardBody(skill.Name, $"{skill.Version} · {skill.Author} · {CategoryName(skill.Category)}");
		if (!string.IsNullOrWhiteSpace(skill.Description)) body.Children.Add(new TextBlock {Text = skill.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")});
		if (skill.Tags.Count > 0) body.Children.Add(new TextBlock {Text = string.Join("  ·  ", skill.Tags), Foreground = Brush("SettingsSecondaryBrush")});
		WrapPanel actions = new() {Margin = new Thickness(0, 8, 0, 0)};
		if (viewModel.ShowMarketplace)
		{
			Button install = Button(NativeSettingsResources.Get("common.install"), () => _ = RunAsync(() => viewModel.InstallMarketplaceAsync(skill)), accent: true);
			install.IsEnabled = !viewModel.InstalledIds.Contains(skill.Id);
			actions.Children.Add(install);
		}
		else
		{
			ToggleSwitch enabled = new() {IsChecked = skill.Enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
			enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleAsync(skill, enabled.IsChecked == true));
			actions.Children.Add(enabled);
		}
		Button details = Button(NativeSettingsResources.Get("skills.details"), () => _ = RunAsync(() => ShowSkillDetailsAsync(viewModel, skill)));
		actions.Children.Add(details);
		if (!viewModel.ShowMarketplace)
		{
			Button edit = Button(skill.Source == "builtin" ? ParityText("创建副本", "Create a copy") : NativeSettingsResources.Get("common.edit"), () => _ = RunAsync(() => EditSkillAsync(viewModel, skill)));
			actions.Children.Add(edit);
			Button uninstall = Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallSkillAsync(viewModel, skill)), danger: true);
			if (!string.Equals(skill.Source, "builtin", StringComparison.OrdinalIgnoreCase)) actions.Children.Add(uninstall);
		}
		foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 8, 8);
		body.Children.Add(actions);
		return WrapCard(body);
	}

	private async Task StartBrowserTaskAsync(AutomationSettingsViewModel viewModel)
	{
		string? actions = await NativeSettingsDialogs.PromptAsync(Owner(), NativeSettingsResources.Get("automation.browserTask"), NativeSettingsResources.Get("automation.taskActions"), "[{\"type\":\"wait\",\"milliseconds\":1000}]", multiline: true).ConfigureAwait(true);
		if (!string.IsNullOrWhiteSpace(actions)) await viewModel.StartBrowserTaskAsync(actions).ConfigureAwait(true);
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

	private Control ToggleRow(AutomationSettingsViewModel viewModel, string label, bool value, Func<bool, Task> changed, bool enabled = true)
	{
		ToggleSwitch toggle = new() {IsChecked = value, IsEnabled = enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		toggle.IsCheckedChanged += (_, _) => _ = RunAsync(() => changed(toggle.IsChecked == true));
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4)};
		row.Children.Add(new TextBlock {Text = label, Foreground = Brush("SettingsPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center});
		Grid.SetColumn(toggle, 1);
		row.Children.Add(toggle);
		return row;
	}

	private Control ToolCard(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		StackPanel body = CardBody(tool.Name, $"{tool.Category} · {tool.PermissionLevel}");
		body.Children.Add(new TextBlock {Text = tool.Description, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		WrapPanel actions = new() {Margin = new Thickness(0, 8, 0, 0)};
		ToggleSwitch enabled = new() {IsChecked = tool.Enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleToolAsync(tool, enabled.IsChecked == true));
		actions.Children.Add(enabled);
		actions.Children.Add(Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => ExecuteMcpToolAsync(viewModel, tool)), enabled: tool.Enabled));
		foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 8, 8);
		body.Children.Add(actions);
		return WrapCard(body);
	}

	private async Task UninstallPluginAsync(PluginsSettingsViewModel viewModel, PluginItem plugin)
	{
		if (!await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("plugins.uninstallConfirm"), true).ConfigureAwait(true)) return;
		bool deleteData = await NativeSettingsDialogs.ConfirmAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.deleteData"), true).ConfigureAwait(true);
		PluginUninstallItem result = await viewModel.UninstallAsync(plugin, deleteData).ConfigureAwait(true);
		if (result.RequiresRestart) await NativeSettingsDialogs.ShowMessageAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.restart")).ConfigureAwait(true);
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

	private bool UpdateComplexPage()
	{
		if (_complexBody is null || _complexOwner != _viewModel || _complexRoot != _root
			|| _complexState is null || _complexRender is null || _viewModel is null) return false;
		if (_busyText is not null) _busyText.Opacity = _viewModel.IsBusy ? 1 : 0;
		if (_errorText is not null)
		{
			_errorText.Text = _viewModel.ErrorMessage;
			_errorText.IsVisible = !string.IsNullOrWhiteSpace(_viewModel.ErrorMessage);
		}
		_complexBody.IsEnabled = !_viewModel.IsBusy;
		// 保存失败时也重新投影宿主状态，让开关回滚到实际值。
		string fingerprint = _complexState() + "\n" + _viewModel.ErrorMessage;
		if (string.Equals(fingerprint, _complexFingerprint, StringComparison.Ordinal)) return true;
		_complexBody.Children.Clear();
		_complexRender(_complexBody);
		_complexFingerprint = fingerprint;
		return true;
	}

	private void BuildMcp(StackPanel root, McpSettingsViewModel viewModel)
	{
		Button servers = Button(NativeSettingsResources.Get("mcp.servers"), () => { viewModel.ShowTools = false; Build(); });
		Button tools = Button(NativeSettingsResources.Get("mcp.tools"), () => { viewModel.ShowTools = true; Build(); });
		root.Children.Add(ParityActions(servers, tools,
			Button(NativeSettingsResources.Get("mcp.import"), () => _ = RunAsync(() => ImportMcpAsync(viewModel))),
			Button(NativeSettingsResources.Get("mcp.add"), () => _ = RunAsync(() => EditMcpAsync(viewModel, null)), accent: true),
			Button(ParityText("刷新", "Refresh"), () => _ = RunAsync(() => viewModel.RefreshAsync()))));
		TextBox search = new() {Text = viewModel.SearchText, PlaceholderText = NativeSettingsResources.Get("mcp.search")};
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? string.Empty;
		root.Children.Add(search);
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new
			{
				viewModel.ShowTools, viewModel.SearchText,
				Servers = viewModel.Servers.Select(server => new {server.Id, server.Name, server.Status, server.ErrorMessage, server.ResourceCount, server.HasEnvironment, server.SecretIssue, Tools = server.Tools.Select(McpToolFingerprint)}),
				Tools = viewModel.BuiltinTools.Select(McpToolFingerprint),
			}),
			list =>
			{
				servers.Classes.Set("accent", !viewModel.ShowTools);
				tools.Classes.Set("accent", viewModel.ShowTools);
				if (viewModel.ShowTools)
				{
					if (viewModel.FilteredTools.Count == 0) list.Children.Add(Empty(NativeSettingsResources.Get("mcp.noItems")));
					foreach (McpToolItem tool in viewModel.FilteredTools) list.Children.Add(ToolCard(viewModel, tool));
				}
				else
				{
					if (viewModel.FilteredServers.Count == 0) list.Children.Add(Empty(NativeSettingsResources.Get("mcp.noItems")));
					foreach (McpServerItem server in viewModel.FilteredServers) list.Children.Add(ServerCard(viewModel, server));
				}
			});
	}

	private Control ServerCard(McpSettingsViewModel viewModel, McpServerItem server)
	{
		string status = server.Status switch
		{
			"connected" => NativeSettingsResources.Get("mcp.connected"),
			"connecting" => ParityText("连接中", "Connecting"),
			"error" => ParityText("连接失败", "Connection failed"),
			_ => NativeSettingsResources.Get("mcp.disconnected"),
		};
		StackPanel body = CardBody(server.Name, $"{status} · {server.Tools.Count} {NativeSettingsResources.Get("mcp.tools")} · {server.ResourceCount} {ParityText("资源", "resources")}");
		if (!string.IsNullOrWhiteSpace(server.ErrorMessage)) body.Children.Add(Empty(server.ErrorMessage));
		if (!string.IsNullOrWhiteSpace(server.SecretIssue)) body.Children.Add(Empty($"{ParityText("环境变量状态", "Environment status")}: {server.SecretIssue}"));
		if (server.HasEnvironment) body.Children.Add(Empty(ParityText("已保存加密环境变量", "Encrypted environment variables saved")));
		body.Children.Add(ParityActions(
			Button(server.Status == "connected" ? NativeSettingsResources.Get("common.stop") : NativeSettingsResources.Get("common.start"),
				() => _ = RunAsync(() => server.Status == "connected" ? viewModel.DisconnectAsync(server) : viewModel.ConnectAsync(server)), enabled: server.Status != "connecting"),
			Button(NativeSettingsResources.Get("common.edit"), () => _ = RunAsync(() => EditMcpAsync(viewModel, server))),
			Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => DeleteMcpAsync(viewModel, server)), danger: true)));
		if (server.Tools.Count > 0)
		{
			StackPanel toolList = new() {Spacing = 10};
			foreach (McpToolItem tool in server.Tools)
			{
				StackPanel row = CardBody(tool.Name, tool.Description);
				row.Children.Add(Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => ExecuteMcpToolAsync(viewModel, tool)), enabled: server.Status == "connected"));
				toolList.Children.Add(row);
			}
			body.Children.Add(new Expander {Header = NativeSettingsResources.Get("mcp.tools"), Content = toolList, HorizontalAlignment = HorizontalAlignment.Stretch});
		}
		return WrapCard(body);
	}

	private void RenderPlugins(StackPanel root, PluginsSettingsViewModel viewModel)
	{
		StackPanel trust = CardBody(NativeSettingsResources.Get("plugins.confirmTrust"), NativeSettingsResources.Get("plugins.trust"));
		Button confirm = Button(viewModel.TrustConfirmed ? NativeSettingsResources.Get("common.enable") : NativeSettingsResources.Get("plugins.confirmTrust"), () => _ = RunAsync(async () =>
		{
			if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("plugins.confirmTrust"), NativeSettingsResources.Get("plugins.trust")).ConfigureAwait(true)) await viewModel.ConfirmTrustAsync().ConfigureAwait(true);
		}), accent: true, enabled: !viewModel.TrustConfirmed && !viewModel.SafeMode);
		trust.Children.Add(confirm);
		if (viewModel.SafeMode) trust.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("plugins.safeMode"), Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
		root.Children.Add(WrapCard(trust));
		Button install = Button(NativeSettingsResources.Get("plugins.install"), () => _ = RunAsync(() => viewModel.InstallLocalAsync()), accent: true, enabled: viewModel.CanInstall);
		root.Children.Add(install);
		if (viewModel.Plugins.Count == 0)
		{
			root.Children.Add(Empty(NativeSettingsResources.Get("plugins.noItems")));
			return;
		}
		foreach (PluginItem plugin in viewModel.Plugins) root.Children.Add(PluginCard(viewModel, plugin));
	}
}
