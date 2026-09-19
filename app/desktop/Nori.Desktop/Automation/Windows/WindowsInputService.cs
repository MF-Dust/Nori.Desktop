using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Automation;

namespace Nori.Desktop.Automation.Windows;

/// <summary>仅执行单击、滚动、受限按键和 Unicode 文本输入。</summary>
public sealed class WindowsInputService
{
	private readonly WindowsWindowService _windows;
	private readonly IWindowsInputNativeApi _native;
	public WindowsInputService(WindowsWindowService? windows = null, IWindowsInputNativeApi? native = null) { _windows = windows ?? new(); _native = native ?? CreateNative(); }
	private static IWindowsInputNativeApi CreateNative()
	{
		if (OperatingSystem.IsWindows()) return new Win32InputNativeApi();
		return new UnsupportedInputNativeApi();
	}
	public WindowsAutomationAvailability Availability => WindowsAutomationAvailability.Current;

	/// <summary>校验目标和 Core 策略后通过 SendInput 执行动作。</summary>
	public WindowsAutomationResult Execute(nint target, AutomationAction action, AutomationPolicy policy)
	{
		ArgumentNullException.ThrowIfNull(action); ArgumentNullException.ThrowIfNull(policy);
		if (!Availability.IsAvailable) return new(false, Availability.Reason);
		if (!policy.TryValidate(action, out string? error)) return new(false, error);
		WindowsTargetValidationResult targetResult = _windows.ValidateTarget(target);
		if (!targetResult.IsValid) return new(false, targetResult.Reason);
		if (!TryBuild(action, out List<WindowsInputPacket> packets, out error)) return new(false, error);
		return _native.TrySendInput(target, packets, out WindowsInputSendFailure failure) ? WindowsAutomationResult.Success : new(false, failure.Reason);
	}

	private bool TryBuild(AutomationAction action, out List<WindowsInputPacket> packets, out string? error)
	{
		packets = []; error = null;
		switch (action)
		{
			case ClickAction click:
				if (!_native.TryGetVirtualScreenBounds(out AutomationBounds screen) || !screen.Contains(click.X, click.Y)) { error = "点击坐标不在虚拟屏幕内"; return false; }
			(int x, int y) = Absolute(click.X, click.Y, screen);
			packets.Add(new(WindowsInputPacketKind.MouseMove, AbsoluteX: x, AbsoluteY: y)); packets.Add(new(WindowsInputPacketKind.MouseDown)); packets.Add(new(WindowsInputPacketKind.MouseUp)); return true;
			case ScrollAction scroll:
				if (scroll.DeltaY != 0) packets.Add(new(WindowsInputPacketKind.MouseWheel, MouseData: scroll.DeltaY));
				if (scroll.DeltaX != 0) packets.Add(new(WindowsInputPacketKind.MouseWheel, Flags: 0x1000, MouseData: scroll.DeltaX));
				return packets.Count > 0;
			case KeyPressAction key:
				if (!TryVirtualKey(key.Key, out ushort vk)) { error = "键盘动作不在白名单内"; return false; }
				packets.Add(new(WindowsInputPacketKind.Keyboard, VirtualKey: vk)); packets.Add(new(WindowsInputPacketKind.Keyboard, VirtualKey: vk, Flags: 2)); return true;
			case TypeTextAction text:
				foreach (char character in text.Text) { packets.Add(new(WindowsInputPacketKind.Keyboard, ScanCode: character, Flags: 4)); packets.Add(new(WindowsInputPacketKind.Keyboard, ScanCode: character, Flags: 6)); }
				return packets.Count > 0;
			default: error = "自动化动作类型不在白名单内"; return false;
		}
	}

	private static (int X, int Y) Absolute(int x, int y, AutomationBounds screen)
	{
		long xRange = Math.Max(1, (long)screen.Width - 1), yRange = Math.Max(1, (long)screen.Height - 1);
		return ((int)Math.Clamp(((long)x - screen.Left) * 65535 / xRange, 0, 65535), (int)Math.Clamp(((long)y - screen.Top) * 65535 / yRange, 0, 65535));
	}

	private static bool TryVirtualKey(string key, out ushort value)
	{
		value = key.ToUpperInvariant() switch { "ENTER" => 13, "TAB" => 9, "ESCAPE" => 27, "BACKSPACE" => 8, "DELETE" => 46, "HOME" => 36, "END" => 35, "PAGEUP" => 33, "PAGEDOWN" => 34, "ARROWUP" => 38, "ARROWDOWN" => 40, "ARROWLEFT" => 37, "ARROWRIGHT" => 39, "SPACE" => 32, _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]), _ => 0 };
		return value != 0;
	}
}

/// <summary>SendInput 的 Win32 封装。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32InputNativeApi : IWindowsInputNativeApi
{
	[StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public nint Extra; } // NOSONAR -- 原生 ABI 结构体仅用于互操作，不参与相等比较
	[StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public nint Extra; } // NOSONAR -- 原生 ABI 结构体仅用于互操作，不参与相等比较
	[StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; } // NOSONAR -- 原生 ABI 结构体仅用于互操作，不参与相等比较
	[StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; } // NOSONAR -- 原生 ABI 结构体仅用于互操作，不参与相等比较
	[DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
	[DllImport("user32.dll")] private static extern nint GetForegroundWindow();
	[DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

	public bool TryGetVirtualScreenBounds(out AutomationBounds bounds)
	{
		int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
		if (width <= 0 || height <= 0) { bounds = default; return false; }
		bounds = new(left, top, width, height); return true;
	}
	public bool TrySendInput(nint target, IReadOnlyList<WindowsInputPacket> packets, out WindowsInputSendFailure failure)
	{
		if (GetForegroundWindow() != target) { failure = new(0, "目标窗口已失去前台焦点"); return false; }
		Input[] inputs = packets.Select(ToNative).ToArray();
		uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
		if (sent == inputs.Length) { failure = default; return true; }
		int code = Marshal.GetLastWin32Error(); failure = new(code, code == 5 ? "SendInput 被 UIPI 拒绝" : "SendInput 未能完整发送输入事件"); return false;
	}
	private static Input ToNative(WindowsInputPacket packet) => packet.Kind switch
	{
		WindowsInputPacketKind.MouseMove => new() { Data = new Union { Mouse = new() { X = packet.AbsoluteX, Y = packet.AbsoluteY, Flags = 0xC001 } } },
		WindowsInputPacketKind.MouseDown => new() { Data = new Union { Mouse = new() { Flags = 2 } } },
		WindowsInputPacketKind.MouseUp => new() { Data = new Union { Mouse = new() { Flags = 4 } } },
		WindowsInputPacketKind.MouseWheel => new() { Data = new Union { Mouse = new() { Data = unchecked((uint)packet.MouseData), Flags = packet.Flags == 0 ? 0x800u : packet.Flags } } },
		WindowsInputPacketKind.Keyboard => new() { Type = 1, Data = new Union { Keyboard = new() { Vk = packet.VirtualKey, Scan = packet.ScanCode, Flags = packet.Flags } } },
		_ => throw new ArgumentOutOfRangeException(nameof(packet)),
	};
}
