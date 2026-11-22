using Nori.Core.Platform;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Tests;

public sealed class WorkspaceSettingsPageTests
{
	[Theory]
	[InlineData(SessionType.X11)]
	[InlineData(SessionType.Wayland)]
	public void ApprovalNotificationDescription_Linux说明通知按钮与应用内回退(SessionType session)
	{
		SettingsText description = WorkspaceSettingsPage.ApprovalNotificationDescription(session);

		Assert.Contains("Linux 桌面通知", description.Chinese);
		Assert.Contains("允许或拒绝", description.Chinese);
		Assert.Contains("否则请使用应用内卡片", description.Chinese);
		Assert.DoesNotContain("开始菜单", description.Chinese);
		Assert.DoesNotContain("Windows", description.Chinese);
		Assert.Contains("Linux desktop notification", description.English);
		Assert.Contains("allow or deny", description.English);
		Assert.Contains("Otherwise, use the in-app card.", description.English);
		Assert.DoesNotContain("Start menu", description.English);
		Assert.DoesNotContain("Windows", description.English);
	}

	[Fact]
	public void ApprovalNotificationDescription_Windows说明首次发送与关闭时的快捷方式行为()
	{
		SettingsText description = WorkspaceSettingsPage.ApprovalNotificationDescription(SessionType.Windows);

		Assert.Contains("第一次发送通知时会创建开始菜单快捷方式", description.Chinese);
		Assert.Contains("关闭此设置时会移除", description.Chinese);
		Assert.Contains("The first notification creates a Start menu shortcut", description.English);
		Assert.Contains("Turning this setting off removes the shortcut", description.English);
	}

	[Theory]
	[InlineData(SessionType.MacOS)]
	[InlineData(SessionType.Unknown)]
	public void ApprovalNotificationDescription_不支持的平台仅说明应用内确认(SessionType session)
	{
		SettingsText description = WorkspaceSettingsPage.ApprovalNotificationDescription(session);

		Assert.Contains("不支持系统授权通知", description.Chinese);
		Assert.Contains("应用内的卡片", description.Chinese);
		Assert.Contains("not supported on this platform", description.English);
		Assert.Contains("in-app card", description.English);
	}
}
