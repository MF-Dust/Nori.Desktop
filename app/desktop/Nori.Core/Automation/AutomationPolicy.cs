namespace Nori.Core.Automation;

/// <summary>自动化坐标边界，右侧和底部边界不包含在内。</summary>
public readonly record struct AutomationBounds
{
	public int Left { get; }
	public int Top { get; }
	public int Width { get; }
	public int Height { get; }

	/// <summary>创建坐标边界。</summary>
	public AutomationBounds(int left, int top, int width, int height)
	{
		if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
		Left = left;
		Top = top;
		Width = width;
		Height = height;
	}

	/// <summary>判断坐标是否位于边界内。</summary>
	public bool Contains(int x, int y) =>
		(long)x >= Left && (long)x < Left + (long)Width &&
		(long)y >= Top && (long)y < Top + (long)Height;
}

/// <summary>自动化安全策略；策略是不可变的。</summary>
public sealed record AutomationPolicy
{
	/// <summary>默认的本地桌面策略。</summary>
	public static AutomationPolicy Default { get; } = new(
		AutomationCapability.Pointer | AutomationCapability.Keyboard | AutomationCapability.Scroll,
		new AutomationBounds(0, 0, 1920, 1080));

	public AutomationCapability Capabilities { get; }
	public AutomationBounds ScreenBounds { get; }
	public int MaxTextLength { get; }
	public int MaxScrollDelta { get; }

	/// <summary>创建自动化策略。</summary>
	public AutomationPolicy(AutomationCapability capabilities, AutomationBounds screenBounds, int maxTextLength = 2000, int maxScrollDelta = 5000)
	{
		if (maxTextLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxTextLength));
		if (maxScrollDelta <= 0) throw new ArgumentOutOfRangeException(nameof(maxScrollDelta));
		Capabilities = capabilities;
		ScreenBounds = screenBounds;
		MaxTextLength = maxTextLength;
		MaxScrollDelta = maxScrollDelta;
	}

	/// <summary>返回动作所需能力。</summary>
	public AutomationCapability RequiredCapability(AutomationActionKind kind) => kind switch
	{
		AutomationActionKind.Click => AutomationCapability.Pointer,
		AutomationActionKind.TypeText or AutomationActionKind.KeyPress => AutomationCapability.Keyboard,
		AutomationActionKind.Scroll => AutomationCapability.Scroll,
		_ => AutomationCapability.None,
	};

	/// <summary>校验动作，错误信息不包含输入正文。</summary>
	public bool TryValidate(AutomationAction action, out string? error)
	{
		ArgumentNullException.ThrowIfNull(action);
		if (action is not ClickAction and not TypeTextAction and not KeyPressAction and not ScrollAction)
		{
			error = "自动化动作类型不在白名单内";
			return false;
		}

		AutomationCapability required = RequiredCapability(action.Kind);
		if (required == AutomationCapability.None || (Capabilities & required) != required)
		{
			error = "自动化策略不允许此动作";
			return false;
		}

		switch (action)
		{
			case ClickAction click when !ScreenBounds.Contains(click.X, click.Y):
				error = "点击坐标超出自动化边界"; return false;
			case TypeTextAction text when string.IsNullOrEmpty(text.Text):
				error = "输入文本不能为空"; return false;
			case TypeTextAction text when text.Text.Length > MaxTextLength:
				error = "输入文本长度超出策略限制"; return false;
			case KeyPressAction key when !AutomationKeySet.IsAllowed(key.Key):
				error = "键盘动作不在白名单内"; return false;
			case ScrollAction scroll when scroll.DeltaX == 0 && scroll.DeltaY == 0:
				error = "滚动增量不能为零"; return false;
			case ScrollAction scroll when Math.Abs((long)scroll.DeltaX) > MaxScrollDelta || Math.Abs((long)scroll.DeltaY) > MaxScrollDelta:
				error = "滚动增量超出策略限制"; return false;
			default:
				break;
		}
		error = null;
		return true;
	}
}

internal static class AutomationKeySet
{
	private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"Enter", "Tab", "Escape", "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
		"ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space",
	};

	public static bool IsAllowed(string key) => !string.IsNullOrWhiteSpace(key) &&
		(NamedKeys.Contains(key) || (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])));
}
