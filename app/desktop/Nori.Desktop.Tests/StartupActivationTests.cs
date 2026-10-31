using Avalonia.Threading;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task 应用已创建但启动装配未完成时保留激活请求(bool fromBackgroundThread) => WithSettingsUiAsync(async () =>
	{
		Program.ConsumePendingActivation();
		try
		{
			App app = new();
			if (fromBackgroundThread)
			{
				await Task.Run(app.ActivateMainWindow);
				await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			}
			else app.ActivateMainWindow();

			Assert.True(Program.ConsumePendingActivation());
			Assert.False(Program.ConsumePendingActivation());
		}
		finally { Program.ConsumePendingActivation(); }
	});
}
