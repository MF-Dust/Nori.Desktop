using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public class NativeSettingsDialogTests
{
	[Fact]
	public Task 自定义确认文案保留危险样式且关闭视为取消() => BridgeCommandsTests.WithSettingsUiAsync(async () =>
	{
		Window owner = new() { Width = 800, Height = 600 };
		owner.Show();
		try
		{
			Task<bool> accepted = NativeSettingsDialogs.ConfirmAsync(owner, "删除", "确认删除？", true, "执行删除", "先留着");
			Window dialog = Assert.Single(owner.OwnedWindows);
			Button confirm = dialog.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "执行删除");
			Button cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "先留着");
			Assert.Contains("danger", confirm.Classes);
			Assert.DoesNotContain("accent", confirm.Classes);
			confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(await accepted);

			Task<bool> declined = NativeSettingsDialogs.ConfirmAsync(owner, "删除", "确认删除？", true, "执行删除", "先留着");
			Window second = Assert.Single(owner.OwnedWindows);
			Button secondCancel = second.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "先留着");
			secondCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(await declined);

			Task<bool> dismissed = NativeSettingsDialogs.ConfirmAsync(owner, "删除", "确认删除？", false, "执行删除", "先留着");
			Window third = Assert.Single(owner.OwnedWindows);
			Button plain = third.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "执行删除");
			Assert.DoesNotContain("danger", plain.Classes);
			third.Close();
			Assert.False(await dismissed);
		}
		finally
		{
			owner.Close();
		}
	});
}
