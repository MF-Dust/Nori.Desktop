using Avalonia.Controls;

namespace Nori.Desktop.Windows;

/// <summary>
/// 把用户关闭变成隐藏。
///
/// Avalonia 在 <c>Closing</c> 回调抛异常时不会采纳 <c>Cancel</c>，Win32 会接着销毁窗口。
/// 平台实现被释放后，托盘再次 <c>Show</c> 会抛 “Cannot re-show a closed window”（NORI-A）。
/// </summary>
internal static class WindowClose
{
	internal static void CancelAndHide(WindowClosingEventArgs args, bool allowClose, Action hide, Action<Exception>? onFailure = null)
	{
		if (allowClose) return;
		args.Cancel = true;
		try
		{
			hide();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			onFailure?.Invoke(exception);
		}
	}
}
