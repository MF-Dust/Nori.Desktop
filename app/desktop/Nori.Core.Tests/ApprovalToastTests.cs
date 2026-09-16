using System.Xml;
using System.Xml.Linq;
using Nori.Core.Notifications;

namespace Nori.Core.Tests;

/// <summary>
/// 授权通知的 XML 与激活参数。
///
/// 这一层值得单独测，是因为它的失败方式**不报错**：toast XML 解析不过时 Windows
/// 既不抛也不记日志，通知只是不出现。而参数那串会穿过系统再回来，属于外部输入。
/// </summary>
public sealed class ApprovalToastTests
{
	private static ApprovalNotice Notice(string tool = "writeFile", string level = "confirm",
		string? summary = null, string? description = null) => new()
	{
		RequestId = "approval-" + Guid.NewGuid().ToString("N"),
		ToolName = tool,
		PermissionLevel = level,
		ArgumentSummary = summary,
		Description = description,
	};

	// ── XML ────────────────────────────────────────────────────────────────

	[Fact]
	public void 组出来的是一份合法XML()
	{
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(Notice(summary: "给未来的你.txt"), english: false));

		Assert.Equal("toast", parsed.Root!.Name.LocalName);
		Assert.Equal(2, parsed.Descendants("action").Count());
	}

	/// <summary>
	/// 参数摘要里装的是模型写的文本。引号、&amp;、尖括号都会出现，漏一个就是静默不弹。
	/// </summary>
	[Theory]
	[InlineData("""{"path":"a&b.txt","content":"<script>"}""")]
	[InlineData("""她说："这是 Nori 写的" & 那是你的""")]
	[InlineData("""'单引号' "双引号" <标签> &实体;""")]
	public void 模型写的文本转义之后仍然解得开(string dirty)
	{
		string xml = ApprovalToastXml.Build(Notice(summary: dirty), english: false);

		// 解得开就说明转义没漏。解不开会抛 XmlException, 正是线上「通知有时候不出来」的成因。
		XDocument parsed = XDocument.Parse(xml);
		Assert.NotNull(parsed.Root);
	}

	[Fact]
	public void 空参数摘要不会留下一个空文本节点()
	{
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(Notice(summary: "   "), english: false));

		// 标题、正文、attribution 三条；摘要是空白时不该再多一条空的。
		Assert.Equal(3, parsed.Descendants("text").Count());
	}

	[Fact]
	public void 没有摘要时退回工具自己的说明()
	{
		string xml = ApprovalToastXml.Build(Notice(summary: null, description: "把文本写进工作目录"), english: false);

		Assert.Contains("把文本写进工作目录", xml, StringComparison.Ordinal);
	}

	[Fact]
	public void 过长的摘要自己截断()
	{
		string longSummary = new('字', ApprovalToastXml.MaxSummaryLength * 3);
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(Notice(summary: longSummary), english: false));

		string detail = parsed.Descendants("text").ElementAt(2).Value;
		Assert.Equal(ApprovalToastXml.MaxSummaryLength + 1, detail.Length);   // 含省略号
		Assert.EndsWith("…", detail, StringComparison.Ordinal);
	}

	/// <summary>换行会把三行额度吃光，正文就被挤出可视区域。</summary>
	[Fact]
	public void 摘要里的换行压成空格()
	{
		XDocument parsed = XDocument.Parse(
			ApprovalToastXml.Build(Notice(summary: "第一行\r\n第二行\n第三行"), english: false));

		string detail = parsed.Descendants("text").ElementAt(2).Value;
		Assert.DoesNotContain('\n', detail);
		Assert.Equal("第一行 第二行 第三行", detail);
	}

	/// <summary>
	/// dangerous 用 reminder 一直留着，其余用 long。
	/// 这一档是「接管鼠标键盘」那类，错过一次的代价和一条普通确认不是一个量级。
	/// </summary>
	[Fact]
	public void 危险操作的通知不会自己消失()
	{
		XDocument dangerous = XDocument.Parse(ApprovalToastXml.Build(Notice(level: "dangerous"), english: false));
		XDocument confirm = XDocument.Parse(ApprovalToastXml.Build(Notice(level: "confirm"), english: false));

		Assert.Equal("reminder", dangerous.Root!.Attribute("scenario")?.Value);
		Assert.Null(confirm.Root!.Attribute("scenario"));
		Assert.Equal("long", confirm.Root.Attribute("duration")?.Value);
	}

	/// <summary>点「拒绝」不该把整个应用拽到前台。</summary>
	[Fact]
	public void 两个按钮都是后台激活()
	{
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(Notice(), english: false));

		foreach (XElement action in parsed.Descendants("action"))
			Assert.Equal("background", action.Attribute("activationType")?.Value);
	}

	[Fact]
	public void 按钮带的是本条授权的id()
	{
		ApprovalNotice notice = Notice();
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(notice, english: false));

		string[] arguments = [.. parsed.Descendants("action").Select(a => a.Attribute("arguments")!.Value)];
		Assert.Equal(ToastActivation.Encode(ToastAction.Allow, notice.RequestId), arguments[0]);
		Assert.Equal(ToastActivation.Encode(ToastAction.Deny, notice.RequestId), arguments[1]);
	}

	/// <summary>界面语言说了算，不跟系统区域 —— 否则同一个应用会说两种话。</summary>
	[Fact]
	public void 英文界面出英文通知()
	{
		string english = ApprovalToastXml.Build(Notice(), english: true);

		Assert.Contains("Allow", english, StringComparison.Ordinal);
		Assert.Contains("Deny", english, StringComparison.Ordinal);
		Assert.DoesNotContain("允许", english, StringComparison.Ordinal);
	}

	/// <summary>工具名本身也可能带需要转义的字符（外部 MCP 工具不在我们手里）。</summary>
	[Fact]
	public void 工具名也要转义()
	{
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(Notice(tool: "fetch<&>Page"), english: false));

		Assert.Contains("fetch<&>Page", parsed.Descendants("text").ElementAt(1).Value, StringComparison.Ordinal);
	}

	// ── 激活参数 ───────────────────────────────────────────────────────────

	[Theory]
	[InlineData(ToastAction.Allow)]
	[InlineData(ToastAction.Deny)]
	[InlineData(ToastAction.Open)]
	public void 编出去再解回来是同一个(ToastAction action)
	{
		const string id = "approval-0123456789abcdef";

		(ToastAction back, string? requestId) = ToastActivation.Parse(ToastActivation.Encode(action, id));

		Assert.Equal(action, back);
		Assert.Equal(id, requestId);
	}

	/// <summary>
	/// 这串东西出过进程，回来的一律当外部输入。**认不出来绝不能回落到允许。**
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	[InlineData("action=allow")]               // 有动作没 id：不知道解哪一条
	[InlineData("id=approval-1")]              // 有 id 没动作：不知道要做什么
	[InlineData("action=ALLOW&id=approval-1")] // 大小写不同就是另一个词
	[InlineData("action=&id=approval-1")]
	[InlineData("action=allow&id=")]
	[InlineData("action=allow&id=approval-1&action=deny")] // 后一个覆盖前一个, 但至少不是 allow
	public void 认不出来的一律不算允许(string? raw)
	{
		(ToastAction action, string? id) = ToastActivation.Parse(raw);

		Assert.NotEqual(ToastAction.Allow, action);
		if (action == ToastAction.Unknown) Assert.Null(id);
	}

	[Fact]
	public void 多余的键忽略掉()
	{
		(ToastAction action, string? id) = ToastActivation.Parse("foo=bar&action=deny&id=approval-9&baz=qux");

		Assert.Equal(ToastAction.Deny, action);
		Assert.Equal("approval-9", id);
	}

	/// <summary>带分隔符的 id 解回来边界就不可靠了，编码阶段直接拒绝。</summary>
	[Theory]
	[InlineData("approval&1")]
	[InlineData("approval=1")]
	public void 带分隔符的id不许编码(string bad) =>
		Assert.Throws<ArgumentException>(() => ToastActivation.Encode(ToastAction.Allow, bad));

	[Fact]
	public void 未知动作不许编码() =>
		Assert.Throws<ArgumentException>(() => ToastActivation.Encode(ToastAction.Unknown, "approval-1"));

	/// <summary>真实形状的 id（Guid:N）能走完一整圈。</summary>
	[Fact]
	public void 真实id能走完一圈()
	{
		string id = $"approval-{Guid.NewGuid():N}";
		ApprovalNotice notice = Notice() with {RequestId = id};
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(notice, english: false));

		string allowArgs = parsed.Descendants("action").First().Attribute("arguments")!.Value;
		(ToastAction action, string? back) = ToastActivation.Parse(allowArgs);

		Assert.Equal(ToastAction.Allow, action);
		Assert.Equal(id, back);
	}

	/// <summary>点正文不是一个决定，只是把卡片带到前台。</summary>
	[Fact]
	public void 点正文解出来是Open()
	{
		ApprovalNotice notice = Notice();
		XDocument parsed = XDocument.Parse(ApprovalToastXml.Build(notice, english: false));

		(ToastAction action, string? id) = ToastActivation.Parse(parsed.Root!.Attribute("launch")!.Value);

		Assert.Equal(ToastAction.Open, action);
		Assert.Equal(notice.RequestId, id);
	}

	// ── 注册指纹 ───────────────────────────────────────────────────────────

	/// <summary>三项都没变就别重写 —— 每次启动都重建快捷方式会让它在开始菜单反复冒头。</summary>
	[Fact]
	public void 同样的三项算出同一个指纹()
	{
		Guid clsid = Guid.NewGuid();
		string first = ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", clsid);
		string again = ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", clsid);

		Assert.Equal(first, again);
		Assert.False(ToastRegistrationStamp.NeedsWrite(first, again));
	}

	/// <summary>便携版可以被整个搬走；搬走之后快捷方式指向的还是旧路径。</summary>
	[Fact]
	public void 换了目录就要重写()
	{
		Guid clsid = Guid.NewGuid();
		string before = ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", clsid);
		string after = ToastRegistrationStamp.Compute(@"E:\Nori\Nori.exe", "A.B", clsid);

		Assert.True(ToastRegistrationStamp.NeedsWrite(before, after));
	}

	/// <summary>Windows 路径大小写不敏感；当成两回事会导致每次启动都重写。</summary>
	[Theory]
	[InlineData(@"d:\nori\nori.exe")]
	[InlineData("D:/Nori/Nori.exe")]
	[InlineData(@"D:\Nori\Nori.exe\")]
	public void 路径写法不同但指的是同一个就不重写(string variant)
	{
		Guid clsid = Guid.NewGuid();
		string canonical = ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", clsid);

		Assert.False(ToastRegistrationStamp.NeedsWrite(canonical,
			ToastRegistrationStamp.Compute(variant, "A.B", clsid)));
	}

	[Fact]
	public void 没存过指纹就要写() =>
		Assert.True(ToastRegistrationStamp.NeedsWrite(null, "whatever"));

	[Fact]
	public void 换了AUMID或CLSID也要重写()
	{
		Guid clsid = Guid.NewGuid();
		string baseline = ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", clsid);

		Assert.True(ToastRegistrationStamp.NeedsWrite(baseline,
			ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.C", clsid)));
		Assert.True(ToastRegistrationStamp.NeedsWrite(baseline,
			ToastRegistrationStamp.Compute(@"D:\Nori\Nori.exe", "A.B", Guid.NewGuid())));
	}

	[Fact]
	public void 不弹的那个实现什么都不做()
	{
		INativeNotifier notifier = NullNativeNotifier.Instance;

		Assert.False(notifier.Available);
		notifier.Show(Notice());
		notifier.Hide("approval-1");
	}
}
