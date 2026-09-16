using System.Security;
using System.Text;

namespace Nori.Core.Notifications;

/// <summary>
/// 授权通知的 toast XML。
///
/// 单独拎出来是因为这一段是纯函数，而它周围全是碰不到测试的 COM 调用：
/// 转义漏一个 &amp; 整条通知就静默不弹（Windows 解析失败不报错，也不留日志），
/// 这类错误只有在 XML 这一层测才抓得住。
///
/// 用 ToastGeneric 模板。正文最多三行，第三行走 attribution（系统会把它排在底部
/// 并压暗），所以把「危险操作」这类分级信息放那儿，正文留给「她要做什么」。
/// </summary>
public static class ApprovalToastXml
{
	/// <summary>参数摘要在通知里的长度上限。超了系统会自己截断，截断点不可控，不如自己来。</summary>
	public const int MaxSummaryLength = 90;

	/// <summary>
	/// 组一条。
	///
	/// <paramref name="english"/> 走界面语言，不跟系统区域 —— 用户在设置里把 Nori 调成
	/// 中文，通知却弹英文，是同一个应用说两种话。
	/// </summary>
	public static string Build(ApprovalNotice notice, bool english)
	{
		ArgumentNullException.ThrowIfNull(notice);

		string title = english ? "Tool approval" : "工具执行确认";
		string body = english
			? $"Nori wants to use {notice.ToolName}"
			: $"Nori 要用 {notice.ToolName}";
		string detail = Clamp(FirstNonEmpty(notice.ArgumentSummary, notice.Description));
		string attribution = Level(notice.PermissionLevel, english);

		// dangerous 用 reminder：它会一直留在屏幕上直到你处理。
		// 这一档本来就是「接管鼠标键盘」那类，错过一次的代价和一条普通确认不是一个量级。
		// 其余用 long（约 25 秒）—— 错过了还有应用内的卡片，不必霸屏。
		bool sticky = notice.PermissionLevel == "dangerous";
		string scenario = sticky ? " scenario=\"reminder\"" : " duration=\"long\"";

		StringBuilder xml = new();
		xml.Append("<toast activationType=\"background\" launch=\"")
			.Append(Escape(ToastActivation.Open(notice.RequestId)))
			.Append('"').Append(scenario).Append('>');
		xml.Append("<visual><binding template=\"ToastGeneric\">");
		xml.Append("<text>").Append(Escape(title)).Append("</text>");
		xml.Append("<text>").Append(Escape(body)).Append("</text>");
		if (detail.Length > 0) xml.Append("<text>").Append(Escape(detail)).Append("</text>");
		xml.Append("<text placement=\"attribution\">").Append(Escape(attribution)).Append("</text>");
		xml.Append("</binding></visual>");

		xml.Append("<actions>");
		Button(xml, english ? "Allow" : "允许执行", ToastActivation.Encode(ToastAction.Allow, notice.RequestId));
		Button(xml, english ? "Deny" : "拒绝", ToastActivation.Encode(ToastAction.Deny, notice.RequestId));
		xml.Append("</actions>");
		xml.Append("</toast>");
		return xml.ToString();
	}

	/// <summary>
	/// 两个按钮都是 background 激活。
	///
	/// 用 foreground 的话点「拒绝」会把整个应用拽到前台 —— 你正在别的窗口里做事，
	/// 拒绝一个工具不该打断你。background 仍然会调到 COM 激活器，只是不抢焦点。
	/// </summary>
	private static void Button(StringBuilder xml, string content, string arguments) =>
		xml.Append("<action activationType=\"background\" content=\"")
			.Append(Escape(content))
			.Append("\" arguments=\"")
			.Append(Escape(arguments))
			.Append("\"/>");

	private static string Level(string permissionLevel, bool english) => permissionLevel switch
	{
		"dangerous" => english ? "Dangerous action" : "危险操作",
		"safe" => english ? "Routine action" : "常规操作",
		_ => english ? "Needs confirmation" : "需要确认",
	};

	private static string FirstNonEmpty(params string?[] values)
	{
		foreach (string? value in values)
			if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
		return "";
	}

	/// <summary>压成一行再截断。通知里换行会把三行的额度吃光，正文就看不见了。</summary>
	private static string Clamp(string value)
	{
		string flat = value.ReplaceLineEndings(" ").Trim();
		while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ", StringComparison.Ordinal);
		return flat.Length <= MaxSummaryLength ? flat : flat[..MaxSummaryLength] + "…";
	}

	/// <summary>
	/// XML 转义。
	///
	/// 参数摘要里装的是模型写的文本 —— 路径、JSON、她自己编的一句话，引号和 &amp;
	/// 都会出现。漏一个，Windows 解析失败就是**静默不弹**：没有异常，没有日志，
	/// 现象只是「通知有时候不出来」。
	/// </summary>
	private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
}
