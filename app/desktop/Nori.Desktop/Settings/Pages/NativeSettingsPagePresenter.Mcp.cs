using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Settings.Pages;

public sealed partial class NativeSettingsPagePresenter
{
	private void BuildMcp(StackPanel root, McpSettingsViewModel viewModel)
	{
		int sync = 0;
		ToggleButton servers = Choice(NativeSettingsResources.Get("mcp.servers"));
		ToggleButton tools = Choice(NativeSettingsResources.Get("mcp.tools"));
		servers.IsChecked = !viewModel.ShowTools;
		tools.IsChecked = viewModel.ShowTools;
		servers.IsCheckedChanged += (_, _) => OnSegment(servers, !viewModel.ShowTools, () => viewModel.ShowTools = false);
		tools.IsCheckedChanged += (_, _) => OnSegment(tools, viewModel.ShowTools, () => viewModel.ShowTools = true);
		Button import = Button(NativeSettingsResources.Get("mcp.import"), () => _ = RunAsync(() => NativeCatalogImportDialogs.ShowMcpImportAsync(Owner(), viewModel)));
		root.Children.Add(ActionGroup(servers, tools, import,
			Button(NativeSettingsResources.Get("mcp.add"), () => _ = RunAsync(() => EditMcpAsync(viewModel, null)), accent: true),
			Button(NativeSettingsResources.Get("common.refresh"), () => _ = RunAsync(() => viewModel.RefreshAsync()))));
		string[] categoryKeys = McpSettingsViewModel.ServerStatusKeys.ToArray();
		TextBox search = new()
		{
			Name = "McpSearch",
			Text = viewModel.SearchText,
			PlaceholderText = NativeSettingsResources.Get("mcp.search"),
			MinWidth = 80,
		};
		Avalonia.Automation.AutomationProperties.SetName(search, NativeSettingsResources.Get("mcp.search"));
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? "";
		ComboBox category = new() {Name = "McpCategory", MinWidth = 148};
		category.SelectionChanged += (_, _) =>
		{
			if (sync > 0 || category.SelectedIndex < 0 || category.SelectedIndex >= categoryKeys.Length) return;
			viewModel.SelectedCategory = categoryKeys[category.SelectedIndex];
		};
		Grid filters = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8};
		filters.Children.Add(search);
		Grid.SetColumn(category, 1);
		filters.Children.Add(category);
		root.Children.Add(filters);
		InitializeComplexBody(root, viewModel,
			() => string.Join('\n',
			[
				viewModel.ShowTools.ToString(),
				viewModel.SearchText,
				viewModel.SelectedCategory,
				viewModel.SafeMode.ToString(),
				viewModel.ConnectedCount.ToString(),
				string.Join(',', viewModel.Servers.Select(server => $"{server.Id}:{server.Status}:{server.Tools.Count}:{server.ResourceCount}:{server.HasEnvironment}:{server.HasHeaders}:{server.SecretIssue}:{server.HeaderSecretIssue}:{server.ErrorMessage}")),
				string.Join(',', viewModel.ToolGroups.SelectMany(group => group.Tools.Select(tool => $"{group.Key}:{tool.Name}:{tool.Enabled}:{tool.PermissionLevel}"))),
			]),
			list =>
			{
				sync++;
				servers.IsChecked = !viewModel.ShowTools;
				tools.IsChecked = viewModel.ShowTools;
				string[] nextKeys = viewModel.ShowTools ? viewModel.ToolCategoryKeys.ToArray() : McpSettingsViewModel.ServerStatusKeys.ToArray();
				if (!nextKeys.SequenceEqual(categoryKeys))
				{
					categoryKeys = nextKeys;
					category.ItemsSource = categoryKeys.Select(McpCategoryLabel).ToArray();
				}
				else if (category.ItemsSource is null)
				{
					category.ItemsSource = categoryKeys.Select(McpCategoryLabel).ToArray();
				}
				int index = Array.IndexOf(categoryKeys, viewModel.SelectedCategory);
				if (index < 0) index = 0;
				if (category.SelectedIndex != index) category.SelectedIndex = index;
				sync--;
				if (viewModel.SafeMode) list.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.safeMode"), Foreground = Brush("SettingsWarningBrush"), TextWrapping = TextWrapping.Wrap});
				list.Children.Add(McpSummary(viewModel));
				if (viewModel.ShowTools) RenderToolGroups(list, viewModel);
				else RenderServers(list, viewModel);
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

	private Control McpSummary(McpSettingsViewModel viewModel)
	{
		string text = viewModel.ShowTools
			? string.Format(NativeSettingsResources.Get("mcp.summaryTools"), viewModel.ToolGroups.Sum(group => group.Tools.Count))
			: string.Format(NativeSettingsResources.Get("mcp.summaryServers"), viewModel.Servers.Count, viewModel.ConnectedCount);
		return new TextBlock {Text = text, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap};
	}

	private static string McpCategoryLabel(string key) => key switch
	{
		"all" => NativeSettingsResources.Get("common.all"),
		"connected" => NativeSettingsResources.Get("mcp.connected"),
		"disconnected" => NativeSettingsResources.Get("mcp.disconnected"),
		"error" => NativeSettingsResources.Get("mcp.failed"),
		_ when key.StartsWith("mcp.group.", StringComparison.Ordinal) => NativeSettingsResources.Get(key),
		_ when key.StartsWith("mcp:", StringComparison.Ordinal) => key["mcp:".Length..],
		_ => NativeSettingsResources.Get("mcp.group." + key) is { } label && label != "mcp.group." + key ? label : key,
	};

	private void RenderServers(StackPanel list, McpSettingsViewModel viewModel)
	{
		if (viewModel.FilteredServers.Count == 0)
		{
			list.Children.Add(Empty(McpEmptyText(viewModel, servers: true)));
			return;
		}
		list.Children.Add(CardGrid(viewModel.FilteredServers.Select(server => ServerCard(viewModel, server))));
	}

	private void RenderToolGroups(StackPanel list, McpSettingsViewModel viewModel)
	{
		if (viewModel.FilteredToolGroups.Count == 0)
		{
			list.Children.Add(Empty(McpEmptyText(viewModel, servers: false)));
			return;
		}
		foreach (McpToolGroup group in viewModel.FilteredToolGroups)
		{
			list.Children.Add(new TextBlock
			{
				Text = GroupTitle(group),
				FontWeight = FontWeight.SemiBold,
				Foreground = Brush("SettingsPrimaryBrush"),
				Margin = new Thickness(0, 8, 0, 0),
			});
			list.Children.Add(CardGrid(group.Tools.Select(tool => ToolCard(viewModel, tool))));
		}
	}

	private static string GroupTitle(McpToolGroup group) =>
		group.Label.StartsWith("mcp.group.", StringComparison.Ordinal) ? NativeSettingsResources.Get(group.Label) : group.Label;

	private static string McpEmptyText(McpSettingsViewModel viewModel, bool servers)
	{
		bool filtered = viewModel.SearchText.Trim().Length > 0 || !string.Equals(viewModel.SelectedCategory, "all", StringComparison.OrdinalIgnoreCase);
		if (filtered) return NativeSettingsResources.Get("mcp.emptyFilter");
		return servers ? NativeSettingsResources.Get("mcp.emptyServers") : NativeSettingsResources.Get("mcp.emptyTools");
	}

	private async Task EditMcpAsync(McpSettingsViewModel viewModel, McpServerItem? existing)
	{
		McpServerDraft draft = existing is null
			? McpSettingsViewModel.NewDraft(NativeSettingsResources.Get("mcp.add"))
			: await viewModel.LoadDraftAsync(existing).ConfigureAwait(true);
		await NativeMcpServerEditor.ShowAsync(Owner(), viewModel, draft, existing is not null, existing?.HasEnvironment == true, existing?.HasHeaders == true).ConfigureAwait(true);
	}

	private async Task DeleteMcpAsync(McpSettingsViewModel viewModel, McpServerItem server)
	{
		if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("common.delete"), NativeSettingsResources.Get("mcp.deleteConfirm"), true).ConfigureAwait(true))
			await viewModel.DeleteAsync(server).ConfigureAwait(true);
	}

	private async Task ExecuteMcpToolAsync(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		string schema = tool.InputSchema.ValueKind == JsonValueKind.Object
			? JsonSerializer.Serialize(tool.InputSchema, new JsonSerializerOptions {WriteIndented = true})
			: "{}";
		string? json = await NativeSettingsDialogs.PromptAsync(
			Owner(), tool.Title,
			$"{tool.Description}\n\n{NativeSettingsResources.Get("mcp.executeArgs")}\n{schema}",
			McpSettingsViewModel.CreateToolArguments(tool), multiline: true).ConfigureAwait(true);
		if (json is null) return;
		JsonElement result = await viewModel.ExecuteToolAsync(tool, json).ConfigureAwait(true);
		await NativeSettingsDialogs.ShowMessageAsync(Owner(), tool.Title, result.ValueKind == JsonValueKind.Undefined
			? NativeSettingsResources.Get("common.none")
			: JsonSerializer.Serialize(result, new JsonSerializerOptions {WriteIndented = true})).ConfigureAwait(true);
	}

	private Control ServerCard(McpSettingsViewModel viewModel, McpServerItem server)
	{
		string brush = server.Status switch
		{
			"connected" => "SettingsSuccessBrush",
			"connecting" => "SettingsWarningBrush",
			"error" => "SettingsErrorBrush",
			_ => "SettingsSecondaryBrush",
		};
		string status = server.Status switch
		{
			"connected" => NativeSettingsResources.Get("mcp.connected"),
			"connecting" => NativeSettingsResources.Get("mcp.connecting"),
			"error" => NativeSettingsResources.Get("mcp.failed"),
			_ => NativeSettingsResources.Get("mcp.disconnected"),
		};
		StackPanel body = new() {Spacing = 8};
		Grid heading = new() {ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8};
		heading.Children.Add(StatusDot(brush));
		heading.Children.Add(IconBadge("server"));
		Grid.SetColumn(heading.Children[1], 1);
		TextBlock title = new()
		{
			Text = server.Name,
			FontWeight = FontWeight.SemiBold,
			Foreground = Brush("SettingsPrimaryBrush"),
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Grid.SetColumn(title, 2);
		heading.Children.Add(title);
		body.Children.Add(heading);
		body.Children.Add(Pill(status, brush));
		body.Children.Add(new TextBlock
		{
			Text = string.Format(NativeSettingsResources.Get("mcp.summaryTools"), server.Tools.Count) + " · " + string.Format(NativeSettingsResources.Get("mcp.resources"), server.ResourceCount),
			Foreground = Brush("SettingsSecondaryBrush"),
			TextWrapping = TextWrapping.Wrap,
		});
		if (server.HasEnvironment || server.HasHeaders) body.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.secretSaved"), Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		if (!string.IsNullOrWhiteSpace(server.SecretIssue) || !string.IsNullOrWhiteSpace(server.HeaderSecretIssue))
		{
			body.Children.Add(new TextBlock
			{
				Text = NativeSettingsResources.Get("mcp.secretIssue") + " " + string.Join(" · ", new[] {server.SecretIssue, server.HeaderSecretIssue}.Where(item => !string.IsNullOrWhiteSpace(item))),
				Foreground = Brush("SettingsWarningBrush"),
				TextWrapping = TextWrapping.Wrap,
			});
		}
		if (!string.IsNullOrWhiteSpace(server.ErrorMessage))
			body.Children.Add(new TextBlock {Text = server.ErrorMessage, Foreground = Brush("SettingsErrorBrush"), TextWrapping = TextWrapping.Wrap});
		bool connected = string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase);
		bool connecting = string.Equals(server.Status, "connecting", StringComparison.OrdinalIgnoreCase);
		body.Children.Add(ActionGroup(
			Button(connected ? NativeSettingsResources.Get("common.stop") : NativeSettingsResources.Get("common.start"),
				() => _ = RunAsync(() => connected ? viewModel.DisconnectAsync(server) : viewModel.ConnectAsync(server)),
				enabled: !connecting && (connected || !viewModel.SafeMode)),
			Button(NativeSettingsResources.Get("common.edit"), () => _ = RunAsync(() => EditMcpAsync(viewModel, server))),
			Button(NativeSettingsResources.Get("common.delete"), () => _ = RunAsync(() => DeleteMcpAsync(viewModel, server)), danger: true)));
		return WrapCard(body, compact: true);
	}

	private Control ToolCard(McpSettingsViewModel viewModel, McpToolItem tool)
	{
		bool safe = string.Equals(tool.PermissionLevel, "safe", StringComparison.OrdinalIgnoreCase);
		StackPanel body = new() {Spacing = 8};
		body.Children.Add(TitleRow("tool", tool.Title, Pill(tool.PermissionLevel, safe ? "SettingsSuccessBrush" : "SettingsWarningBrush")));
		if (!string.IsNullOrWhiteSpace(tool.Description))
			body.Children.Add(new TextBlock {Text = tool.Description, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		if (!safe) body.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.needsApproval"), Foreground = Brush("SettingsWarningBrush"), TextWrapping = TextWrapping.Wrap});
		ToggleSwitch enabled = new() {IsChecked = tool.Enabled, OnContent = NativeSettingsResources.Get("common.enable"), OffContent = NativeSettingsResources.Get("common.disable")};
		Avalonia.Automation.AutomationProperties.SetName(enabled, tool.Name);
		enabled.IsCheckedChanged += (_, _) => _ = RunAsync(() => viewModel.ToggleToolAsync(tool, enabled.IsChecked == true));
		WrapPanel actions = ActionGroup(enabled);
		if (safe) actions.Children.Add(Button(NativeSettingsResources.Get("common.test"), () => _ = RunAsync(() => ExecuteMcpToolAsync(viewModel, tool)), enabled: tool.Enabled));
		body.Children.Add(actions);
		return WrapCard(body, compact: true);
	}
}
