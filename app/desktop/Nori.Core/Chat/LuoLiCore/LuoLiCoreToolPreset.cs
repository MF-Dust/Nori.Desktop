namespace Nori.Core.Chat.LuoLiCore;

/// <summary>
/// 桌宠这个来源建议开放的工具清单。
///
/// 这份清单**不由本端强制**。工具白名单的权威在对端的 `sources.tools` 列，判据在它的
/// `toolCallableBy()`；本端配不了别人的权限，也不该假装配得了。这里存在的理由只有两个：
/// 一是运维照着它去配那一栏时不必回头翻文档，二是 <see cref="Missing"/> 能把「你以为开了、
/// 其实没开」这件事在界面上说出来 —— 那种不一致排查起来很贵。
///
/// 怎么用：在 LuoLiCore 的 WebUI「SDK 接入」页点开桌宠这个来源，工具白名单一栏勾上
/// <see cref="Recommended"/> 里的名字；或者直接
/// <c>PATCH /api/sdk/sources/&lt;id&gt;/tools</c>，body 是 <c>{"tools": [...]}</c>。
/// </summary>
public static class LuoLiCoreToolPreset
{
	/// <summary>文件读写。桌宠最常用的一组 —— 「帮我看看这个文件」「把这段存下来」。</summary>
	public static readonly IReadOnlyList<string> Files =
	[
		"read_file", "list_dir", "glob", "grep",
		"write_file", "edit_file", "move_file", "delete_file",
	];

	/// <summary>执行代码。默认在对端的沙箱里跑，不是直接落在宿主机上。</summary>
	public static readonly IReadOnlyList<string> Code = ["bash", "python"];

	/// <summary>上网。</summary>
	public static readonly IReadOnlyList<string> Web = ["web_search", "web_fetch"];

	/// <summary>翻本会话的聊天记录。</summary>
	public static readonly IReadOnlyList<string> Archive =
	[
		"search_messages", "read_messages", "get_message", "context_stats",
	];

	/// <summary>长期记忆。未配置白名单时对端默认给的就是这一组。</summary>
	public static readonly IReadOnlyList<string> Memory = ["memory_read", "memory_write"];

	/// <summary>
	/// 自我扩展：她可以给自己写工具、写技能、装 MCP。
	///
	/// <c>write_plugin</c> 与 <c>install_mcp</c> 写完之后在对端是**禁用状态**，要 owner 在
	/// WebUI 里看过源码才能启用 —— 插件以 bot 进程的完整权限运行。这道闸在对端，本端不重复。
	/// </summary>
	public static readonly IReadOnlyList<string> SelfExtend =
	[
		"define_tool", "delete_tool", "list_tools",
		"write_skill", "install_skill", "write_plugin",
		"install_mcp", "list_mcp",
	];

	/// <summary>上面六组的合集，去重后按名字排序。</summary>
	public static readonly IReadOnlyList<string> Recommended =
		[.. new[] { Files, Code, Web, Archive, Memory, SelfExtend }
			.SelectMany(group => group)
			.Distinct(StringComparer.Ordinal)
			.OrderBy(name => name, StringComparer.Ordinal)];

	/// <summary>
	/// 角色轴挡住的两件，**配了也调不到**，所以不在 <see cref="Recommended"/> 里。
	///
	/// 对端的权限有两根正交的轴：角色轴问「这个身份够不够格」，来源白名单问「这一档 agent 该
	/// 拿哪些」。SDK 会话的角色恒为 trusted，而这两件的 minRole 是 owner —— 角色轴先生效，
	/// 白名单再怎么配都到不了。把它们列出来是为了让人知道「没给你」不是漏了。
	/// </summary>
	public static readonly IReadOnlyList<string> BlockedByRole =
	[
		"memory_list",            // 列出全部记忆文档，跨来源，owner 专属
		"admin_search_messages",  // 跨会话搜聊天记录，owner 专属
	];

	/// <summary>
	/// 建议开放但对端实际没给的那几件。
	///
	/// 传入 <c>GET /sdk/v1/tools</c> 的结果即可。差集不为空说明白名单没配全，或者对端的运维
	/// 把某件工具整体禁用了 —— 两种情况在界面上都该说出来，而不是等她说「我做不到」。
	/// </summary>
	public static IReadOnlyList<string> Missing(IEnumerable<LuoLiCoreTool> available)
	{
		HashSet<string> have = [.. available.Select(tool => tool.Name)];
		return [.. Recommended.Where(name => !have.Contains(name))];
	}
}
