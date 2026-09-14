using System.Globalization;

namespace Nori.Core.Tools;

/// <summary>
/// 授权档位：她做需要确认的事情之前，要不要先问你。
///
/// 档位只放宽「问不问」，**不放宽她能碰到什么**。工作目录边界、具名任务清单、
/// 读屏开关都在这之外，任何一档都绕不过去 —— 把 bypass 打开也不会让她读到
/// 工作目录以外的文件，或者跑一条你没配过的命令。
/// </summary>
public enum PermissionGear
{
	/// <summary>逐次确认。每一次都弹。默认。</summary>
	Ask,

	/// <summary>本轮记住。同一个工具在这一轮回复里批准过一次，后面不再问。</summary>
	Session,

	/// <summary>完全授权。`confirm` 档一律放行，`dangerous` 仍逐次问。</summary>
	Trusted,

	/// <summary>完全放行。一律不问，包括 `dangerous`。</summary>
	Bypass,
}

/// <summary>这一次要不要问用户。</summary>
public enum PermissionDecision
{
	/// <summary>弹确认框。</summary>
	Ask,

	/// <summary>直接放行，不打扰。</summary>
	Allow,
}

/// <summary>
/// 授权档位的判定与记忆。
///
/// 放在 Core 而不是宿主里：这是一条安全判据，要能被单测钉住。宿主只负责把
/// 判定结果接到确认框上。
/// </summary>
public sealed class ToolPermissionPolicy
{
	/// <summary>设置项键名：当前档位。</summary>
	public const string KeyGear = "permission_gear";

	/// <summary>设置项键名：完全放行的到期时刻（ISO-8601，UTC）。</summary>
	public const string KeyBypassUntil = "permission_bypass_until";

	/// <summary>
	/// 完全放行的有效期。
	///
	/// **不做成永久的。** 这一档下她可以改文件、跑命令、读屏幕而不问一句，
	/// 而人会忘记自己开过它 —— 一个忘掉的无限授权，出事时连「我什么时候同意的」
	/// 都答不上来。到期之后降到完全授权（不是降到逐次确认）：需要确认的那些照常
	/// 放行，只有高风险那一档重新上闸，不至于在你正做事时突然开始逐条弹框。
	/// </summary>
	public static readonly TimeSpan BypassWindow = TimeSpan.FromHours(4);

	/// <summary>
	/// 记住的轮次上限。
	///
	/// 一个 sessionId 只活一轮回复，正常情况下用完就被 <see cref="ForgetTurn"/> 清掉。
	/// 这个上限是兜底：宿主漏调一次清理，不能让这张表一直长。
	/// </summary>
	public const int MaxRememberedTurns = 16;

	private readonly Lock _gate = new();
	private readonly Dictionary<string, HashSet<string>> _remembered = new(StringComparer.Ordinal);
	private readonly Queue<string> _order = new();

	/// <summary>配置里的字符串转档位。认不出的值一律退到最严的一档。</summary>
	public static PermissionGear Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
	{
		"session" => PermissionGear.Session,
		"trusted" => PermissionGear.Trusted,
		"bypass" => PermissionGear.Bypass,
		// 包括空串和拼错的值。放宽授权的默认值必须是「更严」而不是「更松」。
		_ => PermissionGear.Ask,
	};

	/// <summary>档位转配置里的字符串。</summary>
	public static string Format(PermissionGear gear) => gear switch
	{
		PermissionGear.Session => "session",
		PermissionGear.Trusted => "trusted",
		PermissionGear.Bypass => "bypass",
		_ => "ask",
	};

	/// <summary>完全放行从现在起的到期时刻。</summary>
	public static DateTimeOffset BypassDeadline(DateTimeOffset now) => now + BypassWindow;

	/// <summary>把到期时刻写成配置里存的形状。</summary>
	public static string FormatDeadline(DateTimeOffset deadline) =>
		deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

	/// <summary>读回到期时刻；读不出来当作没有。</summary>
	public static DateTimeOffset? ParseDeadline(string? value) =>
		DateTimeOffset.TryParse(
			(value ?? "").Trim(), CultureInfo.InvariantCulture,
			DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
			? parsed
			: null;

	/// <summary>
	/// 真正生效的档位。
	///
	/// 存的是 bypass、但到期时刻已过（或压根没写）时降到 <see cref="PermissionGear.Trusted"/>。
	/// 只在读取时判定，不改配置 —— 到期不该悄悄改掉用户存的那个值，界面要能同时说出
	/// 「你选的是完全放行」和「它已经到期，现在按完全授权走」。
	/// </summary>
	public static PermissionGear Effective(PermissionGear stored, DateTimeOffset? bypassUntil, DateTimeOffset now) =>
		stored == PermissionGear.Bypass && (bypassUntil is null || now >= bypassUntil)
			? PermissionGear.Trusted
			: stored;

	/// <summary>完全放行还剩多久；不在这一档或已到期时为 null。</summary>
	public static TimeSpan? BypassRemaining(PermissionGear stored, DateTimeOffset? bypassUntil, DateTimeOffset now) =>
		stored == PermissionGear.Bypass && bypassUntil is {} until && until > now ? until - now : null;

	/// <summary>
	/// 这一次要不要问。
	///
	/// <paramref name="permissionLevel"/> 用注册表里那三个值（safe / confirm / dangerous）。
	/// safe 根本不会走到这里（注册表自己就放行了），这里仍然收它，是为了让这个函数
	/// 单独看也说得通。
	/// </summary>
	public PermissionDecision Decide(
		PermissionGear gear, string sessionId, string toolName, string permissionLevel)
	{
		if (string.Equals(permissionLevel, "safe", StringComparison.Ordinal)) return PermissionDecision.Allow;
		bool dangerous = string.Equals(permissionLevel, "dangerous", StringComparison.Ordinal);

		return gear switch
		{
			PermissionGear.Bypass => PermissionDecision.Allow,
			// 高风险那一档在「完全授权」下仍然要问：这一档的语义是「日常的事不用打扰我」，
			// 不是「什么都不用告诉我」。要后者就选完全放行，那是一个明确的选择。
			PermissionGear.Trusted => dangerous ? PermissionDecision.Ask : PermissionDecision.Allow,
			PermissionGear.Session when !dangerous && Remembered(sessionId, toolName) => PermissionDecision.Allow,
			_ => PermissionDecision.Ask,
		};
	}

	/// <summary>这一轮里这个工具批准过没有。</summary>
	public bool Remembered(string sessionId, string toolName)
	{
		lock (_gate)
		{
			return _remembered.TryGetValue(sessionId, out HashSet<string>? tools) && tools.Contains(toolName);
		}
	}

	/// <summary>
	/// 记下「这一轮里这个工具批准过了」。
	///
	/// 只有用户**真的按了允许**才该调到这里。超时、取消、拒绝都不算同意 ——
	/// 把它们也记进来，等于一次没人看见的超时换来了后面整轮的静默放行。
	/// </summary>
	public void Remember(string sessionId, string toolName)
	{
		lock (_gate)
		{
			if (!_remembered.TryGetValue(sessionId, out HashSet<string>? tools))
			{
				tools = new HashSet<string>(StringComparer.Ordinal);
				_remembered[sessionId] = tools;
				_order.Enqueue(sessionId);
				while (_order.Count > MaxRememberedTurns)
				{
					_remembered.Remove(_order.Dequeue());
				}
			}

			tools.Add(toolName);
		}
	}

	/// <summary>一轮回复结束，忘掉它记住的东西。</summary>
	public void ForgetTurn(string sessionId)
	{
		lock (_gate) _remembered.Remove(sessionId);
	}

	/// <summary>当前记着的轮次数，供用例断言清理确实发生了。</summary>
	public int RememberedTurnCount
	{
		get { lock (_gate) return _remembered.Count; }
	}
}
