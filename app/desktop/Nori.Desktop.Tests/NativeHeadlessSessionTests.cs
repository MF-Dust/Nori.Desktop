using Avalonia;
using Avalonia.Threading;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task NativeHeadlessSessionReusesThreadAndIsolatesApplicationsAfterFailure()
	{
		Application? previous = null;
		int? uiThread = null;
		for (int index = 0; index < 8; index++)
		{
			bool fail = index == 3;
			Task dispatch = WithSettingsUiAsync(async () =>
			{
				Assert.True(Dispatcher.UIThread.CheckAccess());
				Application current = Assert.IsAssignableFrom<Application>(Application.Current);
				Assert.NotSame(previous, current);
				previous = current;
				uiThread ??= Environment.CurrentManagedThreadId;
				Assert.Equal(uiThread.Value, Environment.CurrentManagedThreadId);
				await Task.Yield();
				Assert.Equal(uiThread.Value, Environment.CurrentManagedThreadId);
				if (fail) throw new InvalidOperationException("模拟测试失败");
			});
			if (fail)
			{
				InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatch);
				Assert.Equal("模拟测试失败", error.Message);
			}
			else await dispatch;
		}
	}
}
