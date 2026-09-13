using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>
/// 文件访问设置页：她能看哪个文件夹，以及一轮里最多连续用多少次工具。
///
/// 归入 `core` 组，与「AI 大脑」相邻：前者决定模型与推理配置，本页决定可访问的资源范围。
/// </summary>
public sealed class WorkspaceSettingsPage : SettingsPageBase
{
	/// <summary>创建文件访问设置页。</summary>
	public WorkspaceSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(
			service,
			"workspace",
			"core",
			new("文件访问", "File access"),
			new("选择她可以查看和修改的文件夹，并控制单轮工具次数。", "Choose the folder she may read and edit, and cap tool calls per turn."),
			lifetimeToken)
	{
		SettingsSectionViewModel folder = AddSection(new("工作文件夹", "Working folder"));

		// 选择按钮排在输入框之前：多数用户使用选择对话框，手工输入路径是次要入口。
		AddAction(
			folder,
			"pickWorkspace",
			new("选择文件夹…", "Choose folder…"),
			new("留空表示不授予本地文件访问权限。", "Leave empty to grant no local file access."),
			new SettingsCommand(async _ => await PickAsync()));

		AddField(
			folder,
			"workspaceRoot",
			new("文件夹路径", "Folder path"),
			new(
				"文件的查看、搜索与修改均限定在该文件夹内，超出范围的路径一律拒绝。修改文件逐次请求确认。",
				"Reading, searching and editing are limited to this folder; paths outside it are refused. Edits request confirmation each time."),
			SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "", "workspace", "root"),
			"",
			(value, token) => ExecuteAsync(
				"settings_update_workspace",
				new { root = Convert.ToString(value) ?? "" },
				token));

		SettingsSectionViewModel limits = AddSection(new("工具次数", "Tool calls"));
		AddField(
			limits,
			"maxToolIterations",
			new("单轮最多用几次工具", "Tool calls per turn"),
			new(
				"单轮回复中连续调用工具的次数上限。调高不影响不使用工具的对话：循环在模型停止调用工具时结束。",
				"Maximum consecutive tool calls in one reply. Raising it does not affect turns without tool use; the loop ends when the model stops calling tools."),
			// 使用数字框而非滑块：当前呈现层的滑块不显示数值，而该设置项的判读依赖具体数字。
			SettingsEditorKind.Number,
			snapshot => SettingsSnapshotReader.Number(
				snapshot,
				Core.Agent.AgentEngine.DefaultToolIterations,
				"workspace",
				"maxToolIterations"),
			(double)Core.Agent.AgentEngine.DefaultToolIterations,
			(value, token) => ExecuteAsync(
				"settings_update_workspace",
				new { maxToolIterations = (int)Convert.ToDouble(value) },
				token),
			minimum: Core.Agent.AgentEngine.MinToolIterations,
			maximum: Core.Agent.AgentEngine.MaxToolIterationsLimit,
			increment: 1);
	}

	/// <summary>
	/// 打开系统文件夹选择对话框，选定后写回配置。
	///
	/// 用户取消时不做任何变更，特别是不清空已有配置。
	/// </summary>
	private async Task PickAsync()
	{
		JsonElement picked = await ExecuteAsync("settings_pick_workspace");
		if (picked.ValueKind != JsonValueKind.Object
			|| !picked.TryGetProperty("root", out JsonElement root)
			|| root.ValueKind != JsonValueKind.String)
		{
			return;
		}

		await ExecuteAsync("settings_update_workspace", new { root = root.GetString() ?? "" });
	}
}
