namespace Nori.Core.Notifications;

/// <summary>用户在通知上做了什么。</summary>
public enum ToastAction
{
	/// <summary>认不出来。收到这个一律当作什么都没发生 —— 不能猜成允许。</summary>
	Unknown,

	/// <summary>点了「允许执行」。</summary>
	Allow,

	/// <summary>点了「拒绝」。</summary>
	Deny,

	/// <summary>点了通知正文：把授权卡片带到前台，不代表任何决定。</summary>
	Open,
}

/// <summary>
/// 通知按钮上带的那串参数的编解码。
///
/// 这串字符串会穿过 Windows：我们写进 toast XML 的 <c>arguments</c>，用户点击后由
/// 系统原样回传给 COM 激活器。也就是说**它出过进程**，回来的东西要当外部输入校验。
///
/// 格式定死为 <c>action=allow&amp;id=approval-xxxx</c>：
/// - 只认这两个键，多余的键忽略；
/// - action 认不出来就是 <see cref="ToastAction.Unknown"/>，绝不回落到允许；
/// - id 必须非空，且不含 &amp; 与 =，否则解出来的边界就不可靠了。
///
/// 授权 id 本身是 <c>approval-{Guid:N}</c>，猜不出来 —— 这是这条路唯一的凭据，
/// 所以不做额外签名，但也**不接受**任何形式的模糊匹配。
/// </summary>
public static class ToastActivation
{
	private const string ActionKey = "action";
	private const string IdKey = "id";

	/// <summary>点正文（不是按钮）时带的那串。</summary>
	public static string Open(string requestId) => Encode(ToastAction.Open, requestId);

	/// <summary>拼一串。requestId 含分隔符时抛 —— 这是编程错误，不该静默出一串解不回来的参数。</summary>
	public static string Encode(ToastAction action, string requestId)
	{
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		if (requestId.Contains('&', StringComparison.Ordinal) || requestId.Contains('=', StringComparison.Ordinal))
			throw new ArgumentException("授权 id 不能包含 & 或 =", nameof(requestId));
		if (action == ToastAction.Unknown) throw new ArgumentException("不能编码未知动作", nameof(action));
		return $"{ActionKey}={Name(action)}&{IdKey}={requestId}";
	}

	/// <summary>解一串。认不出来返回 Unknown + null，调用方据此忽略整次激活。</summary>
	public static (ToastAction Action, string? RequestId) Parse(string? arguments)
	{
		if (string.IsNullOrEmpty(arguments)) return (ToastAction.Unknown, null);

		ToastAction action = ToastAction.Unknown;
		string? id = null;
		foreach (string piece in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int split = piece.IndexOf('=', StringComparison.Ordinal);
			if (split <= 0) continue;
			string key = piece[..split];
			string value = piece[(split + 1)..];
			if (key == ActionKey) action = ParseAction(value);
			else if (key == IdKey && value.Length > 0) id = value;
		}

		// 有动作没 id 等于不知道该解哪一条；有 id 没动作等于不知道要做什么。缺一个都不算数。
		return action == ToastAction.Unknown || id is null ? (ToastAction.Unknown, null) : (action, id);
	}

	private static string Name(ToastAction action) => action switch
	{
		ToastAction.Allow => "allow",
		ToastAction.Deny => "deny",
		ToastAction.Open => "open",
		_ => "unknown",
	};

	private static ToastAction ParseAction(string value) => value switch
	{
		"allow" => ToastAction.Allow,
		"deny" => ToastAction.Deny,
		"open" => ToastAction.Open,
		_ => ToastAction.Unknown,
	};
}
