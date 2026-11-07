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

	private void BuildPlugins(StackPanel root, PluginsSettingsViewModel viewModel)
	{
		TextBox search = new() {Text = viewModel.SearchText, PlaceholderText = ParityText("搜索插件名称、ID 或描述", "Search plugin name, ID or description")};
		Avalonia.Automation.AutomationProperties.SetName(search, ParityText("搜索插件", "Search plugins"));
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? "";
		root.Children.Add(search);
		CheckBox activeOnly = new() {Content = ParityText("仅运行中", "Active only"), IsChecked = viewModel.ActiveOnly};
		activeOnly.IsCheckedChanged += (_, _) => viewModel.ActiveOnly = activeOnly.IsChecked == true;
		root.Children.Add(ActionGroup(activeOnly, Button(ParityText("刷新", "Refresh"), () => _ = RunAsync(() => viewModel.RefreshAsync()))));
		InitializeComplexBody(root, viewModel,
			() => JsonSerializer.Serialize(new {viewModel.Plugins, viewModel.Actions, viewModel.Pages, viewModel.SettingsPages, viewModel.SafeMode, viewModel.TrustConfirmed, viewModel.SearchText, viewModel.ActiveOnly}),
			body => RenderPlugins(body, viewModel));
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




	private static WrapPanel ActionGroup(params Control[] children)
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
		WrapPanel actions = ActionGroup(Button(NativeSettingsResources.Get("common.details"), () => _ = RunAsync(() => ShowPluginDetailsAsync(plugin))));
		bool ready = plugin.State is not ("loading" or "stopping" or "pending_restart");
		if (plugin.State is "active" or "failed")
			actions.Children.Add(Button(NativeSettingsResources.Get("common.disable"), () => _ = RunAsync(() => viewModel.DisableAsync(plugin)), enabled: ready));
		if (plugin.State is "installed" or "disabled" or "failed")
			actions.Children.Add(Button(plugin.State == "failed" ? ParityText("重试", "Retry") : NativeSettingsResources.Get("common.enable"),
				() => _ = RunAsync(() => viewModel.EnableAsync(plugin)), accent: true, enabled: viewModel.CanInstall && ready));
		if (plugin.State == "active")
			actions.Children.Add(Button(ParityText("重新加载", "Reload"), () => _ = RunAsync(() => viewModel.ReloadAsync(plugin)), enabled: viewModel.CanInstall && ready));
		actions.Children.Add(Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => UninstallPluginAsync(viewModel, plugin)), danger: true, enabled: ready));
		foreach (Nori.Desktop.Windows.PluginPageInfo page in viewModel.SettingsPages.Where(page => page.PluginId == plugin.Id))
			actions.Children.Add(Empty($"{page.Title} · {ParityText("已加入设置导航", "Added to settings navigation")}"));
		foreach (Nori.Desktop.Windows.PluginPageInfo page in viewModel.Pages.Where(page => page.PluginId == plugin.Id))
		{
			string pluginId = page.PluginId;
			string pageId = page.Id;
			actions.Children.Add(Button($"{ParityText("打开", "Open")} · {page.Title}",
				() => _ = RunAsync(() => viewModel.OpenPageAsync(pluginId, pageId)), accent: true));
		}
		foreach (Control action in actions.Children) action.Margin = new Thickness(0, 0, 8, 8);
		body.Children.Add(actions);
		IReadOnlyList<McpToolItem> pluginActions = viewModel.GetActions(plugin);
		if (plugin.State == "active" && pluginActions.Count > 0)
		{
			StackPanel actionList = new() {Spacing = 8};
			foreach (McpToolItem action in pluginActions)
			{
				StackPanel row = CardBody(action.Name, $"{action.PermissionLevel} · {action.Description}", compact: true);
				ToggleSwitch enabled = new() {IsChecked = action.Enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
				Avalonia.Automation.AutomationProperties.SetName(enabled, action.Name);
				enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleActionAsync(action, enabled.IsChecked == true));
				row.Children.Add(ActionGroup(enabled, Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => TestPluginActionAsync(viewModel, action)),
					enabled: viewModel.CanInstall && action.Enabled && action.PermissionLevel == "safe")));
				actionList.Children.Add(row);
			}
			actionList.Children.Add(Empty(ParityText("仅 safe 动作支持手动测试；其他动作通过对话审批执行。", "Only safe actions support manual tests; other actions require conversation approval.")));
			body.Children.Add(new Expander {Header = $"{ParityText("插件动作", "Plugin actions")} · {pluginActions.Count}", Content = actionList, HorizontalAlignment = HorizontalAlignment.Stretch});
		}
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
		root.Children.Add(WrapCard(settings));
		StackPanel vision = CardBody(ParityText("视觉模型", "Vision model"), null);
		vision.Children.Add(Empty(state.VisionReady ? ParityText("视觉模型已就绪", "Vision model ready") : ParityText("视觉模型尚未就绪", "Vision model not ready")));
		root.Children.Add(WrapCard(vision));

		StackPanel capability = CardBody(NativeSettingsResources.Get("automation.capabilities"), NativeSettingsResources.Get("automation.status"));
		foreach (AutomationCapabilityItem item in state.Capabilities)
			capability.Children.Add(new TextBlock {Text = $"{(item.Available ? "✓" : "×")}  {item.Name}{(item.Available ? "" : $": {item.UnavailableReason}")}", Foreground = item.Available ? Brush("SettingsPrimaryBrush") : Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		WrapPanel capabilityActions = new() {Margin = new Thickness(0, 8, 0, 0)};
		capabilityActions.Children.Add(Button(NativeSettingsResources.Get("automation.probe"), () => _ = RunAsync(async () =>
		{
			(bool available, string? reason) = await viewModel.ProbeVisionAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("automation.probe"), available ? NativeSettingsResources.Get("common.enable") : reason ?? NativeSettingsResources.Get("common.none")).ConfigureAwait(true);
		}), enabled: viewModel.IsSupported));
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
			"自动化默认关闭，鼠标、键盘和滚动权限需要分别开启。涉及敏感操作时，任务会等待你的确认。",
			"Automation is off by default. Pointer, keyboard, and scroll permissions are enabled separately. Sensitive actions wait for your approval.")));
		security.Children.Add(Empty(ParityText(
			"浏览器自动化已拆分为可选插件「Nori 浏览器自动化」(io.nori.browser)，安装并启用后在插件页打开控制页面。",
			"Browser automation is now the optional plugin \"Nori Browser Automation\" (io.nori.browser). Install and enable it, then open its page from Plugins.")));
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



	private Control TaskRow(AutomationSettingsViewModel viewModel, AutomationTaskItem task)
	{
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 4)};
		StackPanel details = new() {Spacing = 2};
		details.Children.Add(new TextBlock {Text = $"{task.TaskKind} · {task.State}", Foreground = Brush("SettingsPrimaryBrush")});
		details.Children.Add(new TextBlock {Text = $"{task.Id} · {task.ProgressCategory}", Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		row.Children.Add(details);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 6};
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


	private async Task TestPluginActionAsync(PluginsSettingsViewModel viewModel, McpToolItem action)
	{
		string schema = action.InputSchema.ValueKind == JsonValueKind.Object ? action.InputSchema.GetRawText() : "{}";
		string? json = await NativeSettingsDialogs.PromptAsync(Owner(), action.Name,
			$"{action.Description}\n\n{ParityText("参数结构", "Input schema")}\n{schema}", McpSettingsViewModel.CreateToolArguments(action), multiline: true).ConfigureAwait(true);
		if (json is null) return;
		JsonElement result = await viewModel.TestActionAsync(action, json).ConfigureAwait(true);
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), action.Name, result.ValueKind == JsonValueKind.Undefined
			? NativeSettingsResources.Get("common.none") : JsonSerializer.Serialize(result, new JsonSerializerOptions {WriteIndented = true})).ConfigureAwait(true);
	}

	private async Task UninstallPluginAsync(PluginsSettingsViewModel viewModel, PluginItem plugin)
	{
		if (!await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("plugins.uninstallConfirm"), true).ConfigureAwait(true)) return;
		bool deleteData = await NativeSettingsDialogs.ConfirmAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.deleteData"), true).ConfigureAwait(true);
		PluginUninstallItem result = await viewModel.UninstallAsync(plugin, deleteData).ConfigureAwait(true);
		if (result.RequiresRestart) await NativeSettingsDialogs.ShowMessageAsync(Owner(), plugin.Name, NativeSettingsResources.Get("plugins.restart")).ConfigureAwait(true);
	}


	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3241", Justification = "调用方使用返回值决定是否跳过完整重建。")]
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
		if (viewModel.FilteredPlugins.Count == 0)
		{
			root.Children.Add(Empty(NativeSettingsResources.Get("plugins.noItems")));
			return;
		}
		foreach (PluginItem plugin in viewModel.FilteredPlugins) root.Children.Add(PluginCard(viewModel, plugin));
	}
}
