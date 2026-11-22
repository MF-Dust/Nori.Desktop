using System.Runtime.InteropServices;
using Nori.Core.Automation;
using static Nori.Core.Platform.LinuxX11WindowProvider;

namespace Nori.Core.Platform;

internal static class LinuxX11Input
{
	private const string LibXtst = "libXtst.so.6";
	private const string MissingXTest = "缺少 X11 XTest 扩展（libXtst.so.6），无法注入键鼠事件";
	private const uint ShiftMask = 1;
	private const uint CoreKeyboard = 0x0100;
	private const int WheelDelta = 120;

	internal enum Status { Succeeded, Failed, TargetNotForeground }

	internal static string? ProbeUnavailableReason()
	{
		try { return WithDisplay(HasXTest) ? null : MissingXTest; }
		catch (DllNotFoundException) { return MissingXTest; }
		catch (EntryPointNotFoundException) { return MissingXTest; }
		catch (InvalidOperationException exception) { return exception.Message; }
	}

	internal static Status Execute(nint target, AutomationAction action, AutomationPolicy policy, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!policy.TryValidate(action, out _)) return Status.Failed;
		try { return WithDisplay(display => Execute(display, target, action, policy, cancellationToken)); }
		catch (DllNotFoundException) { return Status.Failed; }
		catch (EntryPointNotFoundException) { return Status.Failed; }
		catch (InvalidOperationException) { return Status.Failed; }
	}

	private static Status Execute(nint display, nint target, AutomationAction action, AutomationPolicy policy, CancellationToken cancellationToken)
	{
		if (!HasXTest(display)) return Status.Failed;
		Status status = CheckTarget(display, target, out _);
		if (status != Status.Succeeded) return status;

		return action switch
		{
			ClickAction click => Click(display, target, click, policy, cancellationToken),
			ScrollAction scroll => Scroll(display, target, scroll, policy, cancellationToken),
			KeyPressAction key => SendKeys(display, target, [LinuxX11KeyMap.ForKeyPress(key.Key)], cancellationToken),
			TypeTextAction text => LinuxX11KeyMap.TryReadText(text.Text, out List<uint> symbols)
				? SendKeys(display, target, symbols, cancellationToken)
				: throw new InvalidOperationException("X11 文本输入不支持控制字符或无效 Unicode"),
			_ => throw new ArgumentOutOfRangeException(nameof(action)),
		};
	}

	private static Status CheckTarget(nint display, nint target, out AutomationBounds bounds)
	{
		AutomationBounds? visible = GetBounds(display, target);
		bounds = visible.GetValueOrDefault();
		return ValidateTarget(target, visible, GetForegroundWindow(display)) switch
		{
			TargetStatus.Valid => Status.Succeeded,
			TargetStatus.NotForeground => Status.TargetNotForeground,
			_ => Status.Failed,
		};
	}

	internal static bool IsAllowedPoint(int x, int y, AutomationBounds target, AutomationBounds screen, AutomationPolicy policy) =>
		target.Contains(x, y) && screen.Contains(x, y) && policy.ScreenBounds.Contains(x, y);

	private static Status Click(nint display, nint target, ClickAction click, AutomationPolicy policy, CancellationToken cancellationToken)
	{
		Status status = CheckTarget(display, target, out AutomationBounds bounds);
		if (status != Status.Succeeded) return status;
		if (!IsAllowedPoint(click.X, click.Y, bounds, GetScreenBounds(display), policy)
			|| !HitsTarget(display, target, click.X, click.Y)) return Status.Failed;
		cancellationToken.ThrowIfCancellationRequested();
		if (XTestFakeMotionEvent(display, XDefaultScreen(display), click.X, click.Y, 0) == 0) return Status.Failed;

		status = CheckPointer(display, target, policy, out int x, out int y);
		if (status != Status.Succeeded) return status;
		if (x != click.X || y != click.Y) return Status.Failed;
		cancellationToken.ThrowIfCancellationRequested();
		return SendButton(display, 1) ? Status.Succeeded : Status.Failed;
	}

	private static Status Scroll(nint display, nint target, ScrollAction scroll, AutomationPolicy policy, CancellationToken cancellationToken)
	{
		Status status = SendWheel(scroll.DeltaY, 4, 5);
		return status == Status.Succeeded ? SendWheel(scroll.DeltaX, 7, 6) : status;

		Status SendWheel(int delta, uint positive, uint negative)
		{
			uint button = delta > 0 ? positive : negative;
			// X11 滚轮按钮按格发送；沿用 Windows 一格 120 的增量单位。
			for (long remaining = Math.Abs((long)delta); remaining > 0; remaining -= WheelDelta)
			{
				Status current = CheckPointer(display, target, policy, out _, out _);
				if (current != Status.Succeeded) return current;
				cancellationToken.ThrowIfCancellationRequested();
				if (!SendButton(display, button)) return Status.Failed;
			}
			return Status.Succeeded;
		}
	}

	private static Status CheckPointer(nint display, nint target, AutomationPolicy policy, out int x, out int y)
	{
		x = y = 0;
		Status status = CheckTarget(display, target, out AutomationBounds bounds);
		if (status != Status.Succeeded) return status;
		return XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out x, out y, out _, out _, out _) != 0
			&& IsAllowedPoint(x, y, bounds, GetScreenBounds(display), policy)
			&& HitsTarget(display, target, x, y) ? Status.Succeeded : Status.Failed;
	}

	private static bool HitsTarget(nint display, nint target, int x, int y)
	{
		// 从根窗口沿该点命中的子窗口下探，排除覆盖在目标上的其他窗口。
		nint root = XDefaultRootWindow(display);
		nint window = root;
		while (XTranslateCoordinates(display, root, window, x, y, out _, out _, out window) != 0 && window != 0)
			if (window == target) return true;
		return false;
	}

	private static bool SendButton(nint display, uint button) =>
		XTestFakeButtonEvent(display, button, 1, 0) != 0 && XTestFakeButtonEvent(display, button, 0, 0) != 0;

	private static Status SendKeys(nint display, nint target, IReadOnlyList<uint> symbols, CancellationToken cancellationToken)
	{
		if (!TryGetKeyboardState(display, out uint modifiers)) return Status.Failed;
		byte shift = XKeysymToKeycode(display, LinuxX11KeyMap.ShiftLeft);
		Dictionary<uint, LinuxX11KeyMap.Stroke> map = ReadKeyboardMap(display, modifiers, shift != 0);
		LinuxX11KeyMap.Stroke[] strokes = new LinuxX11KeyMap.Stroke[symbols.Count];
		for (int i = 0; i < symbols.Count; i++)
		{
			// 整段文本先验证，避免不可输入的字符留下已输入的前缀。
			if (!map.TryGetValue(symbols[i], out strokes[i]))
				throw new InvalidOperationException("当前 X11 键盘布局无法输入指定按键或字符");
		}

		foreach (LinuxX11KeyMap.Stroke stroke in strokes)
		{
			Status status = CheckTarget(display, target, out _);
			if (status != Status.Succeeded) return status;
			if (!HasKeyboardFocus(display, target)) return Status.TargetNotForeground;
			if (!TryGetKeyboardState(display, out uint current) || current != modifiers) return Status.Failed;
			cancellationToken.ThrowIfCancellationRequested();
			if (!SendKey(display, stroke, shift)) return Status.Failed;
		}
		return Status.Succeeded;
	}

	private static bool HasKeyboardFocus(nint display, nint target)
	{
		// 活动窗口和实际键盘接收方不同；文本只能发往目标或其子窗口。
		XGetInputFocus(display, out nint focus, out _);
		while (focus > 1)
		{
			if (focus == target) return true;
			if (XQueryTree(display, focus, out _, out focus, out nint children, out _) == 0) break;
			XFree(children);
		}
		return false;
	}

	private static bool TryGetKeyboardState(nint display, out uint modifiers)
	{
		int status = XkbGetState(display, CoreKeyboard, out XkbState state);
		modifiers = ((uint)state.Group << 13) | state.LookupMods;
		// 不把合成按键与用户当前按住或锁存的修饰键混成快捷键。
		return status == 0 && state.BaseMods == 0 && state.LatchedMods == 0;
	}

	private static Dictionary<uint, LinuxX11KeyMap.Stroke> ReadKeyboardMap(nint display, uint modifiers, bool canShift)
	{
		XDisplayKeycodes(display, out int minimum, out int maximum);
		Dictionary<uint, LinuxX11KeyMap.Stroke> map = [];
		for (int code = minimum; code <= maximum; code++)
		{
			// XKB 按活动布局组及锁定状态解析，而非假设映射表前两项就是实际字符。
			byte keycode = (byte)code;
			uint plain = LookupKeySym(display, keycode, modifiers);
			uint shifted = canShift ? LookupKeySym(display, keycode, modifiers | ShiftMask) : 0;
			LinuxX11KeyMap.AddKeycode(map, keycode, plain, shifted);
		}
		return map;
	}

	private static uint LookupKeySym(nint display, byte keycode, uint modifiers) =>
		XkbLookupKeySym(display, keycode, modifiers, out _, out nuint symbol) != 0 ? (uint)symbol : 0;

	private static bool SendKey(nint display, LinuxX11KeyMap.Stroke stroke, byte shift)
	{
		if (stroke.Shift && XTestFakeKeyEvent(display, shift, 1, 0) == 0) return false;
		bool sent = XTestFakeKeyEvent(display, stroke.Keycode, 1, 0) != 0 && XTestFakeKeyEvent(display, stroke.Keycode, 0, 0) != 0;
		if (stroke.Shift) sent = (XTestFakeKeyEvent(display, shift, 0, 0) != 0) && sent;
		return sent;
	}

	private static bool HasXTest(nint display) => XTestQueryExtension(display, out _, out _, out _, out _) != 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct XkbState
	{
		public byte Group, LockedGroup;
		public ushort BaseGroup, LatchedGroup;
		public byte Mods, BaseMods, LatchedMods, LockedMods, CompatState, GrabMods, CompatGrabMods, LookupMods, CompatLookupMods;
		public ushort PointerButtons;
	}

	[DllImport(LibXtst)] private static extern int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);
	[DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
	[DllImport(LibXtst)] private static extern int XTestFakeButtonEvent(nint display, uint button, int press, nuint delay);
	[DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(nint display, uint keycode, int press, nuint delay);
	[DllImport(LibX11)] private static extern int XDefaultScreen(nint display);
	[DllImport(LibX11)] private static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);
	[DllImport(LibX11)] private static extern int XQueryPointer(nint display, nint window, out nint root, out nint child,
		out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
	[DllImport(LibX11)] private static extern byte XKeysymToKeycode(nint display, nuint keysym);
	[DllImport(LibX11)] private static extern int XDisplayKeycodes(nint display, out int minimum, out int maximum);
	[DllImport(LibX11)] private static extern int XkbGetState(nint display, uint device, out XkbState state);
	[DllImport(LibX11)] private static extern int XkbLookupKeySym(nint display, byte keycode, uint modifiers, out uint consumed, out nuint keysym);
}
