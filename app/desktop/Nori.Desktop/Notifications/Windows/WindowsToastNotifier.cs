using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Notifications;

namespace Nori.Desktop.Notifications.Windows;

/// <summary>
/// 用 Windows 的系统通知呈现待决授权。
///
/// 这一层只做三件事：把 XML 交给 WinRT、留住 toast 对象以便之后收掉、把点击回传。
/// 内容怎么组、参数怎么编解码都在 <see cref="ApprovalToastXml"/> 和
/// <see cref="ToastActivation"/> 里 —— 那两处是纯函数，有测试；这里全是 COM，没有。
///
/// **每一条路都允许静默失败。** 用户可能在系统设置里关掉了通知、专注助手可能把它
/// 折叠、快捷方式可能建不出来。授权本来就还有应用内的卡片那条路，通知这条断了
/// 不该让工具调用失败。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsToastNotifier : INativeNotifier, IDisposable
{
	private readonly Func<bool> _english;
	private readonly Action<string, string> _log;

	/// <summary>
	/// 显示中的通知。收掉一条必须交回 Show 时**那个同一个** COM 对象，
	/// 所以不能只存 id。
	/// </summary>
	private readonly ConcurrentDictionary<string, ToastNativeApi.IToastNotification> _live = new();

	private ToastNativeApi.IToastNotifier? _notifier;

	internal WindowsToastNotifier(Func<bool> english, Action<string, string> log)
	{
		_english = english;
		_log = log;
	}

	public bool Available => _notifier is not null;

	/// <summary>
	/// 系统对这个 AUMID 的通知设置。0 = 允许；1 = 被这个应用的开关关掉；
	/// 2 = 用户全局关掉；3 = 组策略关掉；4 = 清单关掉。
	/// 用来分「发不出去」和「发出去了但系统不给显示」—— 这两种现象一模一样。
	/// </summary>
	internal int Setting
	{
		get
		{
			try { return _notifier?.GetSetting() ?? -1; }
			catch { return -2; }
		}
	}

	/// <summary>
	/// 准备好通知器。
	///
	/// 调用方要先确保 <see cref="ToastRegistrar"/> 已经写过快捷方式 —— 没有它，
	/// 这里照样能拿到对象，<c>Show</c> 也不报错，屏幕上就是不出现。
	/// </summary>
	internal bool TryStart()
	{
		try
		{
			// Avalonia 的 UI 线程已经是 STA；这里要的是 WinRT 就绪。
			// RPC_E_CHANGED_MODE 表示已经初始化过且模式不同 —— 不是错误。
			int initialized = ToastNativeApi.RoInitialize(ToastNativeApi.RoInitMultiThreaded);
			if (initialized < 0 && initialized != ToastNativeApi.RpcEChangedMode)
				Marshal.ThrowExceptionForHR(initialized);

			ToastNativeApi.IToastNotificationManagerStatics statics = Activation<ToastNativeApi.IToastNotificationManagerStatics>(
				ToastNativeApi.ToastNotificationManagerClass, ToastNativeApi.IidToastNotificationManagerStatics);

			using ToastNativeApi.HString aumid = new(ToastRegistrar.AppUserModelId);
			_notifier = statics.CreateToastNotifierWithId(aumid.Handle);
			return true;
		}
		catch (Exception failure)
		{
			// 这台机器上就是弹不出来。记一行, 然后当它不存在。
			_log("warn", $"系统通知不可用：{failure.GetType().Name}");
			_notifier = null;
			return false;
		}
	}

	public void Show(ApprovalNotice notice)
	{
		if (_notifier is not { } notifier) return;
		try
		{
			string xml = ApprovalToastXml.Build(notice, _english());

			object document = ActivateInstance(ToastNativeApi.XmlDocumentClass);
			using (ToastNativeApi.HString payload = new(xml))
			{
				((ToastNativeApi.IXmlDocumentIO) document).LoadXml(payload.Handle);
			}

			ToastNativeApi.IToastNotificationFactory factory = Activation<ToastNativeApi.IToastNotificationFactory>(
				ToastNativeApi.ToastNotificationClass, ToastNativeApi.IidToastNotificationFactory);
			ToastNativeApi.IToastNotification toast = factory.CreateToastNotification(document);

			// 先记住再 Show：Show 之后用户随时可能点，而 Hide 要拿得到这个对象。
			_live[notice.RequestId] = toast;
			notifier.Show(toast);
		}
		catch (Exception failure)
		{
			_live.TryRemove(notice.RequestId, out _);
			_log("warn", $"发通知失败：{failure.GetType().Name}");
		}
	}

	public void Hide(string requestId)
	{
		if (!_live.TryRemove(requestId, out ToastNativeApi.IToastNotification? toast)) return;
		try
		{
			// 用户已经点掉或系统已经清掉时 Hide 会抛；这不是错误。
			_notifier?.Hide(toast);
		}
		catch
		{
			// 见上。
		}
		finally
		{
			try { Marshal.FinalReleaseComObject(toast); } catch { /* 已释放 */ }
		}
	}

	private static T Activation<T>(string runtimeClass, Guid iid)
	{
		using ToastNativeApi.HString name = new(runtimeClass);
		Guid interfaceId = iid;
		ToastNativeApi.RoGetActivationFactory(name.Handle, ref interfaceId, out object factory);
		return (T) factory;
	}

	private static object ActivateInstance(string runtimeClass)
	{
		ToastNativeApi.IActivationFactory factory = Activation<ToastNativeApi.IActivationFactory>(
			runtimeClass, ToastNativeApi.IidActivationFactory);
		return factory.ActivateInstance();
	}

	public void Dispose()
	{
		foreach (string id in _live.Keys) Hide(id);
		_notifier = null;
	}
}
