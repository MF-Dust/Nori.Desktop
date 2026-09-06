using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;

namespace Nori.Core.Chat;

/// <summary>
/// 动作标记处理
///
/// 对应 Rust 版 chat.rs 的 extract_motion_markers / motion_hint.
/// AI 在回复末尾用 [nori_motion:动作名] 表达动作, 宿主剥掉标记并广播给伴侣窗口播放.
/// </summary>
public static class MotionMarkers
{
	/// <summary>动作标记起始串</summary>
	private const string MarkerStart = "[nori_motion:";

	/// <summary>
	/// 从回复中提取动作标记
	/// </summary>
	/// <returns>剥离标记后的文本, 以及动作名列表</returns>
	public static (string Content, IReadOnlyList<string> Motions) Extract(string content)
	{
		StringBuilder clean = new();
		List<string> motions = [];
		ReadOnlySpan<char> rest = content;
		while (true)
		{
			int start = rest.IndexOf(MarkerStart, StringComparison.Ordinal);
			if (start < 0) break;
			clean.Append(rest[..start]);
			ReadOnlySpan<char> after = rest[(start + MarkerStart.Length)..];
			int end = after.IndexOf(']');
			if (end < 0)
			{
				// 没有闭合的标记原样保留
				clean.Append(MarkerStart);
				rest = after;
				continue;
			}
			string name = after[..end].Trim().ToString();
			if (name.Length > 0) motions.Add(name);
			rest = after[(end + 1)..];
		}
		clean.Append(rest);
		return (clean.ToString(), motions);
	}

	/// <summary>
	/// 从配置读取当前模型动作列表, 组装成提示词附录
	///
	/// 优先读 l2d_motions_&lt;模型id&gt;, 回退全局 l2d_motions; 没有动作时返回空串
	/// </summary>
	public static string BuildHint(ConfigStore config, string modelId)
	{
		string[] keys = modelId.Length == 0 ? ["l2d_motions"] : [$"l2d_motions_{modelId}", "l2d_motions"];
		JsonArray? groups = null;
		foreach (string key in keys)
		{
			if (config.Get(key) is ConfigValue.Json {Value: JsonArray array})
			{
				groups = array;
				break;
			}
		}
		if (groups is null || groups.Count == 0) return string.Empty;

		List<string> lines = [];
		foreach (JsonNode? group in groups)
		{
			if (group is not JsonObject item) continue;
			string name = item["group"]?.GetValue<string>() ?? string.Empty;
			string names = item["names"] is JsonArray list
				? string.Join(", ", list.OfType<JsonValue>().Select(value => value.TryGetValue(out string? text) ? text : null).OfType<string>())
				: string.Empty;
			if (name.Length > 0 && names.Length > 0) lines.Add($"{name}: {names}");
		}
		if (lines.Count == 0) return string.Empty;

		return "\n\n## 当前可用动作\n需要表达动作时, 在回复末尾另起一行附加标记 [nori_motion:动作名], 每行一个, 最多一个, 动作名从下面选择, 没有合适的就不加:\n"
			+ string.Join('\n', lines);
	}
}
