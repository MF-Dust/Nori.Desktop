using Nori.Core.Automation;
using Nori.Core.Platform;

namespace Nori.Core.Tests;

public sealed class LinuxX11InputTests
{
	[Theory]
	[InlineData("Enter", 0xff0dU)]
	[InlineData("Tab", 0xff09U)]
	[InlineData("Escape", 0xff1bU)]
	[InlineData("Backspace", 0xff08U)]
	[InlineData("Delete", 0xffffU)]
	[InlineData("Home", 0xff50U)]
	[InlineData("End", 0xff57U)]
	[InlineData("PageUp", 0xff55U)]
	[InlineData("PageDown", 0xff56U)]
	[InlineData("ArrowUp", 0xff52U)]
	[InlineData("ArrowDown", 0xff54U)]
	[InlineData("ArrowLeft", 0xff51U)]
	[InlineData("ArrowRight", 0xff53U)]
	[InlineData("Space", 0x20U)]
	[InlineData("R", 0x72U)]
	[InlineData("7", 0x37U)]
	public void 白名单按键映射到X11键符号(string key, uint expected)
	{
		Assert.True(AutomationPolicy.Default.TryValidate(new KeyPressAction(key), out _));
		Assert.Equal(expected, LinuxX11KeyMap.ForKeyPress(key));
	}

	[Theory]
	[InlineData("F1")]
	[InlineData("Ctrl")]
	[InlineData("Alt")]
	[InlineData("Shift")]
	[InlineData("Super")]
	[InlineData("Ctrl+R")]
	[InlineData("Return")]
	public void 非白名单按键仍被策略与X11映射拒绝(string key)
	{
		Assert.False(AutomationPolicy.Default.TryValidate(new KeyPressAction(key), out _));
		Assert.Equal(0U, LinuxX11KeyMap.ForKeyPress(key));
		Assert.Equal(LinuxX11Input.Status.Failed, LinuxX11Input.Execute(42, new KeyPressAction(key), AutomationPolicy.Default, default));
	}

	[Fact]
	public void 文本保留大小写和ASCII与Unicode码点()
	{
		Assert.True(LinuxX11KeyMap.TryReadText("aA é中😀", out List<uint> symbols));
		Assert.Equal([0x61U, 0x41U, 0x20U, 0xe9U, 0x01004e2dU, 0x0101f600U], symbols);
	}

	[Fact]
	public void 文本拒绝控制字符和损坏的代理对()
	{
		Assert.False(LinuxX11KeyMap.TryReadText("a\n", out _));
		Assert.False(LinuxX11KeyMap.TryReadText(new string(['a', '\ud800']), out _));
	}

	[Fact]
	public void 键盘映射仅在需要时使用Shift且优先无修饰键的映射()
	{
		Dictionary<uint, LinuxX11KeyMap.Stroke> map = [];
		LinuxX11KeyMap.AddKeycode(map, 38, 'a', 'A');
		LinuxX11KeyMap.AddKeycode(map, 10, '&', '1');
		Assert.Equal(new LinuxX11KeyMap.Stroke(38, false), map['a']);
		Assert.Equal(new LinuxX11KeyMap.Stroke(38, true), map['A']);
		Assert.Equal(new LinuxX11KeyMap.Stroke(10, true), map['1']);
		LinuxX11KeyMap.AddKeycode(map, 87, '1', 0);
		Assert.Equal(new LinuxX11KeyMap.Stroke(87, false), map['1']);
		Assert.False(map.ContainsKey(0));
	}

	[Fact]
	public void 目标必须可见且为当前前台窗口()
	{
		AutomationBounds bounds = new(10, 20, 30, 40);
		Assert.Equal(LinuxX11WindowProvider.TargetStatus.Invalid, LinuxX11WindowProvider.ValidateTarget(42, null, 42));
		Assert.Equal(LinuxX11WindowProvider.TargetStatus.NotForeground, LinuxX11WindowProvider.ValidateTarget(42, bounds, 43));
		Assert.Equal(LinuxX11WindowProvider.TargetStatus.NotForeground, LinuxX11WindowProvider.ValidateTarget(42, bounds, 0));
		Assert.Equal(LinuxX11WindowProvider.TargetStatus.Valid, LinuxX11WindowProvider.ValidateTarget(42, bounds, 42));
	}

	[Fact]
	public void 点击和滚轮位置必须处于目标屏幕及策略交集()
	{
		AutomationBounds target = new(-20, 10, 120, 90);
		AutomationBounds screen = new(0, 0, 80, 100);
		AutomationPolicy policy = new(AutomationCapability.Pointer, new(0, 0, 100, 80));
		Assert.True(LinuxX11Input.IsAllowedPoint(79, 79, target, screen, policy));
		Assert.False(LinuxX11Input.IsAllowedPoint(20, 9, target, screen, policy));
		Assert.False(LinuxX11Input.IsAllowedPoint(80, 20, target, screen, policy));
		Assert.False(LinuxX11Input.IsAllowedPoint(20, 80, target, screen, policy));
		Assert.Equal(LinuxX11Input.Status.Failed, LinuxX11Input.Execute(42, new TypeTextAction("不应执行"), policy, default));
	}
}
