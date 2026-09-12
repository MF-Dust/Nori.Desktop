using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Settings.Pages;

/// <summary>MCP 连接的原生编辑器，草稿验证失败时保留用户输入。</summary>
internal static class NativeMcpServerEditor
{
	/// <summary>编辑元数据和只写环境变量，连接测试与保存分别执行。</summary>
	public static async Task ShowAsync(Window owner, McpSettingsViewModel viewModel, McpServerDraft draft, bool editing, bool hasEnvironment)
	{
		StackPanel body = new() {Spacing = 12, Margin = new Thickness(24)};
		StackPanel fields = new() {Spacing = 12};
		TextBox name = Input(draft.Name);
		ComboBox transport = new() {ItemsSource = new[] {"Stdio", "SSE"}, SelectedIndex = draft.Transport == "sse" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch};
		TextBox command = Input(draft.Command);
		TextBox arguments = Input(JsonSerializer.Serialize(draft.Arguments, new JsonSerializerOptions {WriteIndented = true}), multiline: true);
		TextBox url = Input(draft.Url ?? "");
		TextBox environment = Input("", multiline: true);
		ToggleSwitch enabled = new() {Content = NativeSettingsResources.Get("common.enable"), IsChecked = draft.Enabled};
		ToggleSwitch autoConnect = new() {Content = Text("启动时自动连接", "Connect automatically on startup"), IsChecked = draft.AutoConnect};
		CheckBox clearEnvironment = new() {Content = Text("清除已保存的环境变量", "Clear saved environment variables"), IsVisible = hasEnvironment};
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.name"), name));
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.transport"), transport));
		Control commandField = Field(NativeSettingsResources.Get("mcp.command"), command);
		Control argumentsField = Field(Text("启动参数 · JSON 字符串数组", "Arguments · JSON string array"), arguments);
		Control urlField = Field(NativeSettingsResources.Get("mcp.sseUrl"), url);
		fields.Children.Add(commandField);
		fields.Children.Add(argumentsField);
		fields.Children.Add(urlField);
		fields.Children.Add(Field(NativeSettingsResources.Get("mcp.env"), environment));
		fields.Children.Add(new TextBlock
		{
			Text = hasEnvironment
				? Text("已有环境变量保持加密。留空会保留原值；填写后会替换整个环境变量集合。测试只使用本次填写的值。", "Saved values stay encrypted. Leave blank to preserve them, or enter a replacement set. Tests use only values entered here.")
				: Text("每行填写 NAME=value。环境变量只写入宿主，不会从宿主回传。", "Use NAME=value on each line. Environment variables are write-only and are never returned by the host."),
			TextWrapping = TextWrapping.Wrap,
		});
		fields.Children.Add(clearEnvironment);
		fields.Children.Add(enabled);
		fields.Children.Add(autoConnect);
		void UpdateTransport()
		{
			bool stdio = transport.SelectedIndex == 0;
			commandField.IsVisible = stdio;
			argumentsField.IsVisible = stdio;
			urlField.IsVisible = !stdio;
		}
		transport.SelectionChanged += (_, _) => UpdateTransport();
		clearEnvironment.IsCheckedChanged += (_, _) => environment.IsEnabled = clearEnvironment.IsChecked != true;
		UpdateTransport();
		body.Children.Add(new ScrollViewer
		{
			Content = fields,
			MaxHeight = Math.Max(140, Math.Min(520, owner.Bounds.Height - 220)),
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
		});
		TextBlock feedback = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel")};
		Button test = new() {Content = NativeSettingsResources.Get("common.test")};
		Button save = new() {Content = NativeSettingsResources.Get("common.save")};
		save.Classes.Add("accent");
		actions.Children.Add(cancel);
		actions.Children.Add(test);
		actions.Children.Add(save);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(editing ? NativeSettingsResources.Get("common.edit") : NativeSettingsResources.Get("mcp.add"), body, Math.Min(600, Math.Max(420, owner.Bounds.Width - 48)));
		dialog.MaxHeight = Math.Max(320, owner.Bounds.Height - 24);
		bool busy = false;
		McpServerDraft ReadDraft() => draft with
		{
			Name = (name.Text ?? "").Trim(),
			Transport = transport.SelectedIndex == 1 ? "sse" : "stdio",
			Command = command.Text ?? "",
			Arguments = transport.SelectedIndex == 0 ? McpSettingsViewModel.ParseArgumentList(arguments.Text ?? "") : [],
			Environment = clearEnvironment.IsChecked == true ? new Dictionary<string, string>() : McpSettingsViewModel.ParseEnvironment(environment.Text ?? ""),
			ClearEnvironment = clearEnvironment.IsChecked == true,
			Url = url.Text,
			Enabled = enabled.IsChecked == true,
			AutoConnect = autoConnect.IsChecked == true,
		};
		async Task ExecuteAsync(bool saveChanges)
		{
			if (busy) return;
			busy = true;
			fields.IsEnabled = false;
			test.IsEnabled = false;
			save.IsEnabled = false;
			feedback.Text = NativeSettingsResources.Get("common.working");
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
						? Text("连接成功", "Connection succeeded")
						: result?.ErrorMessage ?? NativeSettingsResources.Get("mcp.disconnected");
				}
			}
			catch (Exception exception)
			{
				feedback.Text = exception.Message;
			}
			finally
			{
				busy = false;
				fields.IsEnabled = true;
				test.IsEnabled = true;
				save.IsEnabled = true;
			}
		}
		cancel.Click += (_, _) => dialog.Close();
		test.Click += async (_, _) => await ExecuteAsync(false).ConfigureAwait(true);
		save.Click += async (_, _) => await ExecuteAsync(true).ConfigureAwait(true);
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	private static TextBox Input(string value, bool multiline = false) => new()
	{
		Text = value,
		AcceptsReturn = multiline,
		TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
		MinHeight = multiline ? 76 : 36,
		MaxLines = multiline ? 8 : 1,
	};

	private static Control Field(string label, Control control)
	{
		StackPanel field = new() {Spacing = 6};
		field.Children.Add(new TextBlock {Text = label, FontWeight = FontWeight.SemiBold});
		field.Children.Add(control);
		return field;
	}

	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;
}
