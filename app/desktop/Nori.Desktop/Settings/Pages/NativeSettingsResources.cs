using Nori.Desktop.Settings;

namespace Nori.Desktop.Settings.Pages;

/// <summary>原生设置复杂页使用的双语资源。</summary>
public static class NativeSettingsResources
{
	private static readonly IReadOnlyDictionary<string, SettingsText> Values = new Dictionary<string, SettingsText>(StringComparer.Ordinal)
	{
		["common.all"] = new("全部", "All"),
		["common.cancel"] = new("取消", "Cancel"),
		["common.close"] = new("关闭", "Close"),
		["common.confirm"] = new("确认", "Confirm"),
		["common.copy"] = new("复制", "Copy"),
		["common.delete"] = new("删除", "Delete"),
		["common.details"] = new("详情", "Details"),
		["common.disable"] = new("禁用", "Disable"),
		["common.edit"] = new("编辑", "Edit"),
		["common.enable"] = new("启用", "Enable"),
		["common.install"] = new("安装", "Install"),
		["common.none"] = new("无", "None"),
		["common.save"] = new("保存", "Save"),
		["common.start"] = new("启动", "Start"),
		["common.stop"] = new("停止", "Stop"),
		["common.test"] = new("测试", "Test"),
		["common.working"] = new("处理中…", "Working…"),

		["skills.installed"] = new("已安装", "Installed"),
		["skills.marketplace"] = new("市场", "Marketplace"),
		["skills.search"] = new("搜索技能", "Search skills"),
		["skills.new"] = new("新建技能", "New skill"),
		["skills.installUrl"] = new("从网址安装", "Install from URL"),
		["skills.noItems"] = new("暂无技能", "No skills"),
		["skills.details"] = new("详情", "Details"),
		["skills.name"] = new("名称", "Name"),
		["skills.description"] = new("描述", "Description"),
		["skills.author"] = new("作者", "Author"),
		["skills.version"] = new("版本", "Version"),
		["skills.icon"] = new("图标", "Icon"),
		["skills.categoryField"] = new("分类", "Category"),
		["skills.tags"] = new("标签（逗号分隔）", "Tags (comma separated)"),
		["skills.tools"] = new("工具", "Tools"),
		["skills.instructions"] = new("使用说明", "Instructions"),
		["skills.url"] = new("技能网址", "Skill URL"),
		["skills.uninstallConfirm"] = new("确定要卸载这个技能吗？", "Uninstall this skill?"),
		["skills.builtin"] = new("内置技能不能卸载。", "Built-in skills cannot be uninstalled."),

		["mcp.servers"] = new("服务器", "Servers"),
		["mcp.tools"] = new("工具", "Tools"),
		["mcp.search"] = new("搜索服务器或工具", "Search servers or tools"),
		["mcp.import"] = new("导入服务器", "Import server"),
		["mcp.add"] = new("添加服务器", "Add server"),
		["mcp.noItems"] = new("暂无项目", "No items"),
		["mcp.connected"] = new("已连接", "Connected"),
		["mcp.disconnected"] = new("未连接", "Disconnected"),
		["mcp.id"] = new("标识", "ID"),
		["mcp.name"] = new("名称", "Name"),
		["mcp.transport"] = new("传输方式", "Transport"),
		["mcp.command"] = new("命令", "Command"),
		["mcp.args"] = new("参数", "Arguments"),
		["mcp.env"] = new("环境变量", "Environment"),
		["mcp.sseUrl"] = new("SSE 地址", "SSE URL"),
		["mcp.url"] = new("导入地址", "Import URL"),
		["mcp.executeArgs"] = new("工具参数（JSON）", "Tool arguments (JSON)"),
		["mcp.deleteConfirm"] = new("确定要删除这个 MCP 服务器吗？", "Delete this MCP server?"),

		["automation.enabled"] = new("启用自动化", "Enable automation"),
		["automation.pointer"] = new("允许鼠标操作", "Allow pointer input"),
		["automation.keyboard"] = new("允许键盘操作", "Allow keyboard input"),
		["automation.scroll"] = new("允许滚动操作", "Allow scrolling"),
		["automation.browser"] = new("浏览器自动化", "Browser automation"),
		["automation.browserStart"] = new("启动浏览器", "Start browser"),
		["automation.browserStop"] = new("停止浏览器", "Stop browser"),
		["automation.browserTask"] = new("浏览器任务", "Browser task"),
		["automation.taskActions"] = new("任务动作（JSON）", "Task actions (JSON)"),
		["automation.capabilities"] = new("能力", "Capabilities"),
		["automation.status"] = new("运行状态", "Status"),
		["automation.probe"] = new("检测视觉能力", "Probe vision"),
		["automation.approvals"] = new("待审批操作", "Pending approvals"),
		["automation.approve"] = new("批准", "Approve"),
		["automation.deny"] = new("拒绝", "Deny"),
		["automation.queued"] = new("排队", "queued"),
		["automation.noTasks"] = new("暂无任务", "No tasks"),
		["automation.stopTask"] = new("停止任务", "Stop task"),
		["automation.stopAll"] = new("停止全部", "Stop all"),
		["automation.audit"] = new("审计记录", "Audit log"),
		["automation.refreshAudit"] = new("刷新记录", "Refresh audit"),
		["automation.noAudit"] = new("暂无审计记录", "No audit records"),

		["plugins.confirmTrust"] = new("确认插件风险", "Confirm plugin risk"),
		["plugins.trust"] = new("插件在进程内运行，启用前请确认你信任其代码。", "Plugins run in-process; confirm that you trust their code before enabling them."),
		["plugins.safeMode"] = new("安全模式下不能运行插件。", "Plugins cannot run in safe mode."),
		["plugins.install"] = new("安装本地插件", "Install local plugin"),
		["plugins.noItems"] = new("暂无插件", "No plugins"),
		["plugins.capabilities"] = new("能力", "Capabilities"),
		["plugins.uninstallConfirm"] = new("确定要卸载这个插件吗？", "Uninstall this plugin?"),
		["plugins.deleteData"] = new("同时删除插件数据吗？", "Delete the plugin data too?"),
		["plugins.restart"] = new("插件已卸载，重启应用后会完成清理。", "The plugin was uninstalled; restart the app to finish cleanup."),

		["debug.warning"] = new("调试操作可能影响应用运行，请谨慎使用。", "Debug actions may affect the application; use them carefully."),
		["debug.diagnostic"] = new("诊断信息", "Diagnostics"),
		["debug.refresh"] = new("刷新", "Refresh"),
		["debug.export"] = new("导出诊断", "Export diagnostics"),
		["debug.openFolder"] = new("打开日志目录", "Open log folder"),
		["debug.logs"] = new("最近日志", "Recent logs"),
		["debug.all"] = new("全部", "All"),
		["debug.clear"] = new("清空日志", "Clear logs"),
		["debug.clearConfirm"] = new("确定要清空最近日志吗？", "Clear recent logs?"),
		["debug.noLogs"] = new("暂无日志", "No logs"),
		["debug.crash"] = new("调试工具", "Debug tools"),
		["debug.gc"] = new("回收内存", "Collect garbage"),
		["debug.released"] = new("已释放字节", "Bytes released"),
		["debug.testLog"] = new("写入测试日志", "Write test log"),
		["debug.uiCrash"] = new("测试 UI 崩溃", "Test UI crash"),
		["debug.backgroundCrash"] = new("测试后台崩溃", "Test background crash"),
		["debug.taskCrash"] = new("测试任务崩溃", "Test task crash"),
		["debug.crashConfirm"] = new("确定要触发崩溃测试吗？", "Trigger the crash test?"),
		["debug.exitConfirm"] = new("此操作可能退出应用，确定继续吗？", "This may exit the app. Continue?"),
	};

	/// <summary>按当前界面语言读取资源；未知键返回键名以保持界面可诊断。</summary>
	public static string Get(string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		return Values.TryGetValue(key, out SettingsText text)
			? text.Resolve(SettingsLocalization.IsEnglish ? "en-US" : "zh-CN")
			: key;
	}
}
