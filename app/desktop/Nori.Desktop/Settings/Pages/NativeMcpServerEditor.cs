using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Settings.Pages;

/// <summary>MCP 连接的原生编辑器。空白的环境变量和请求头表示保留已保存密文。</summary>
internal static class NativeMcpServerEditor
{
	/// <summary>编辑元数据和只写秘密字段。测试与保存使用同一套保留规则。</summary>
	public static async Task ShowAsync(
		Window owner,
		McpSettingsViewModel viewModel,
		McpServerDraft draft,
		bool editing,
		bool hasEnvironment,
		bool hasHeaders = false)
	{
		bool safeMode = viewModel.SafeMode;
		StackPanel body = new() {Spacing = 12, Margin = new Thickness(24)};
		if (safeMode)
		{
			body.Children.Add(new TextBlock
			{
				Text = NativeSettingsResources.Get("mcp.safeMode"),
				Foreground = SettingsBrushes.Resolve(owner, "SettingsWarningBrush"),
				TextWrapping = TextWrapping.Wrap,
			});
		}
		StackPanel fields = new() {Spacing = 12};
		TextBox name = Input(draft.Name);
		int sync = 0;
		string transport = draft.Transport is "http" or "sse" ? draft.Transport : "stdio";
		ToggleButton stdio = Choice("Stdio");
		ToggleButton http = Choice("HTTP");
		ToggleButton sse = Choice("SSE");
		stdio.Name = "McpTransportStdio";
		http.Name = "McpTransportHttp";
		sse.Name = "McpTransportSse";
		WrapPanel transportRow = new() {Orientation = Orientation.Horizontal};
		transportRow.Children.Add(stdio);
		transportRow.Children.Add(http);
		transportRow.Children.Add(sse);
		TextBox command = Input(draft.Command);
		TextBox arguments = Input(McpSettingsViewModel.FormatArgumentLines(draft.Arguments), multiline: true);
		arguments.Name = "McpArguments";
		TextBox url = Input(draft.Url ?? "");
		TextBox environment = Input("", multiline: true);
		environment.Name = "McpEnvironment";
		TextBox headers = Input("", multiline: true);
		headers.Name = "McpHeaders";
		ToggleSwitch enabled = new() {Content = NativeSettingsResources.Get("common.enable"), IsChecked = draft.Enabled};
		ToggleSwitch autoConnect = new()
		{
			Content = NativeSettingsResources.Get("mcp.autoConnect"),
			IsChecked = safeMode ? false : draft.AutoConnect,
			IsEnabled = !safeMode,
		};
		CheckBox clearEnvironment = new() {Content = NativeSettingsResources.Get("mcp.clearEnv"), IsVisible = hasEnvironment};
		CheckBox clearHeaders = new() {Content = NativeSettingsResources.Get("mcp.clearHeaders"), IsVisible = hasHeaders};
		TextBlock urlLabel = new() {Text = NativeSettingsResources.Get("mcp.sseUrl"), FontWeight = FontWeight.SemiBold};
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.name"), name));
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.transport"), transportRow));
		Control commandField = Field(NativeSettingsResources.Get("mcp.command"), command);
		Control argumentsField = Field(NativeSettingsResources.Get("mcp.argsHint"), arguments);
		Control urlField = Field(urlLabel, url);
		Control headersField = Field(NativeSettingsResources.Get("mcp.headers"), headers);
		fields.Children.Add(commandField);
		fields.Children.Add(argumentsField);
		fields.Children.Add(urlField);
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.env"), environment));
		fields.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.envHint"), TextWrapping = TextWrapping.Wrap});
		fields.Children.Add(clearEnvironment);
		fields.Children.Add(headersField);
		fields.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.headerHint"), TextWrapping = TextWrapping.Wrap});
		fields.Children.Add(clearHeaders);
		fields.Children.Add(enabled);
		fields.Children.Add(autoConnect);
		void SyncTransport()
		{
			sync++;
			stdio.IsChecked = transport == "stdio";
			http.IsChecked = transport == "http";
			sse.IsChecked = transport == "sse";
			sync--;
			bool remote = transport is "http" or "sse";
			commandField.IsVisible = !remote;
			argumentsField.IsVisible = !remote;
			urlField.IsVisible = remote;
			headersField.IsVisible = remote;
			clearHeaders.IsVisible = remote && hasHeaders;
			urlLabel.Text = transport == "sse" ? NativeSettingsResources.Get("mcp.sseUrl") : NativeSettingsResources.Get("mcp.http");
		}
		void OnChoice(ToggleButton button, string value, Action select)
		{
			if (sync > 0) return;
			if (button.IsChecked == true)
			{
				select();
				return;
			}
			if (transport != value) return;
			sync++;
			button.IsChecked = true;
			sync--;
		}
		void SelectTransport(string value)
		{
			if (sync > 0) return;
			transport = value;
			SyncTransport();
		}
		stdio.IsCheckedChanged += (_, _) => OnChoice(stdio, "stdio", () => SelectTransport("stdio"));
		http.IsCheckedChanged += (_, _) => OnChoice(http, "http", () => SelectTransport("http"));
		sse.IsCheckedChanged += (_, _) => OnChoice(sse, "sse", () => SelectTransport("sse"));
		clearEnvironment.IsCheckedChanged += (_, _) => environment.IsEnabled = clearEnvironment.IsChecked != true;
		clearHeaders.IsCheckedChanged += (_, _) => headers.IsEnabled = clearHeaders.IsChecked != true;
		SyncTransport();
		body.Children.Add(new ScrollViewer
		{
			Content = fields,
			MaxHeight = Math.Max(140, Math.Min(520, owner.Bounds.Height - 220)),
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
		});
		TextBlock feedback = new() {Name = "McpEditorFeedback", TextWrapping = TextWrapping.Wrap, IsVisible = false};
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel")};
		Button test = new() {Content = NativeSettingsResources.Get("common.test"), IsEnabled = !safeMode};
		Button save = new() {Content = NativeSettingsResources.Get("common.save")};
		save.Classes.Add("accent");
		actions.Children.Add(cancel);
		actions.Children.Add(test);
		actions.Children.Add(save);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(editing ? NativeSettingsResources.Get("common.edit") : NativeSettingsResources.Get("mcp.add"), body, Math.Min(640, Math.Max(440, owner.Bounds.Width - 48)));
		dialog.MaxHeight = Math.Max(320, owner.Bounds.Height - 24);
		bool busy = false;
		McpServerDraft ReadDraft() => draft with
		{
			Name = (name.Text ?? "").Trim(),
			Transport = transport,
			Command = command.Text ?? "",
			Arguments = transport == "stdio" ? McpSettingsViewModel.ParseArgumentLines(arguments.Text ?? "") : [],
			Environment = clearEnvironment.IsChecked == true ? new Dictionary<string, string>() : McpSettingsViewModel.ParseEnvironment(environment.Text ?? ""),
			ClearEnvironment = clearEnvironment.IsChecked == true,
			Headers = clearHeaders.IsChecked == true ? new Dictionary<string, string>() : McpSettingsViewModel.ParseHeaders(headers.Text ?? ""),
			ClearHeaders = clearHeaders.IsChecked == true,
			Url = url.Text,
			Enabled = enabled.IsChecked == true,
			AutoConnect = safeMode ? false : autoConnect.IsChecked == true,
		};
		async Task ExecuteAsync(bool saveChanges)
		{
			if (busy) return;
			busy = true;
			fields.IsEnabled = false;
			test.IsEnabled = false;
			save.IsEnabled = false;
			feedback.Text = NativeSettingsResources.Get("common.working");
			feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsSecondaryBrush");
			feedback.IsVisible = true;
			try
			{
				McpServerDraft updated = ReadDraft();
				if (saveChanges)
				{
					await viewModel.SaveServerAsync(updated).ConfigureAwait(true);
					dialog.Close();
				}
				else
				{
					McpServerItem? result = await viewModel.TestServerAsync(updated).ConfigureAwait(true);
					feedback.Text = result?.Status == "connected"
						? NativeSettingsResources.Get("mcp.connected")
						: result?.ErrorMessage ?? NativeSettingsResources.Get("mcp.disconnected");
					feedback.Foreground = SettingsBrushes.Resolve(dialog, result?.Status == "connected" ? "SettingsSuccessBrush" : "SettingsErrorBrush");
				}
			}
			catch (Exception exception)
			{
				feedback.Text = SettingsErrorText.Resolve(exception);
				feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsErrorBrush");
			}
			finally
			{
				busy = false;
				fields.IsEnabled = true;
				test.IsEnabled = !safeMode;
				save.IsEnabled = true;
			}
		}
		cancel.Click += (_, _) => dialog.Close();
		test.Click += async (_, _) => await ExecuteAsync(false).ConfigureAwait(true);
		save.Click += async (_, _) => await ExecuteAsync(true).ConfigureAwait(true);
		await dialog.ShowDialog(owner).ConfigureAwait(true);

		static ToggleButton Choice(string text)
		{
			ToggleButton button = new() {Content = text, MinHeight = 32, Margin = new Thickness(0, 0, 8, 0)};
			button.Classes.Add("settings-choice");
			return button;
		}
	}

	private static TextBox Input(string value, bool multiline = false) => new()
	{
		Text = value,
		AcceptsReturn = multiline,
		TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
		MinHeight = multiline ? 76 : 36,
		MaxLines = multiline ? 8 : 1,
	};

	private static Control Field(string label, Control control) => Field(new TextBlock {Text = label, FontWeight = FontWeight.SemiBold}, control);

	private static Control Field(Control label, Control control)
	{
		StackPanel field = new() {Spacing = 6};
		field.Children.Add(label);
		field.Children.Add(control);
		return field;
	}
}
