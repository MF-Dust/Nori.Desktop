using System.Runtime.Versioning;

namespace Nori.Desktop.Startup;

/// <summary>
/// 跨平台单实例锁与 Windows 第二实例激活信号。
///
/// 三平台都使用宿主命名 Mutex 保护启动与迁移；Windows 额外用命名事件唤醒第一个
/// 实例的 main 窗口，Linux/macOS 的第二实例则直接退出。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3453", Justification = "实例必须由 TryAcquire 工厂创建以保证互斥体所有权状态一致。")]
internal sealed class SingleInstanceGuard : IDisposable
{
	private const string WindowsMutexName = @"Local\NoriDesktopPet.SingleInstance";
	private const string PortableMutexName = "NoriDesktopPet.SingleInstance";
	private const string ActivationEventName = @"Local\NoriDesktopPet.Activate";

	private readonly Mutex? _mutex;
	private readonly EventWaitHandle? _activationEvent;
	private readonly CancellationTokenSource _listenerCts = new();
	private readonly Action _onActivate;
	private readonly Thread? _listener;
	private bool _ownsMutex;
	private int _disposed;

	private SingleInstanceGuard(
		Mutex? mutex,
		EventWaitHandle? activationEvent,
		bool ownsMutex,
		Action onActivate)
	{
		_mutex = mutex;
		_activationEvent = activationEvent;
		_ownsMutex = ownsMutex;
		_onActivate = onActivate;

		if (_activationEvent is not null)
		{
			_listener = new Thread(Listen)
			{
				IsBackground = true,
				Name = "Nori second-instance listener",
			};
			_listener.Start();
		}
	}

	/// <summary>
	/// 尝试成为第一个实例。返回 null 表示已经向现有实例发送激活信号。
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例获取失败后的句柄清理不能覆盖原始启动错误。")]
	public static SingleInstanceGuard? TryAcquire(Action onActivate, bool signalExisting = true)
	{
		ArgumentNullException.ThrowIfNull(onActivate);
		string mutexName = OperatingSystem.IsWindows() ? WindowsMutexName : PortableMutexName;
		Mutex mutex = new(true, mutexName, out bool createdNew);
		bool ownsMutex = createdNew;
		if (!createdNew)
		{
			try
			{
				ownsMutex = mutex.WaitOne(0);
			}
			catch (AbandonedMutexException)
			{
				ownsMutex = true;
			}

			if (!ownsMutex)
			{
				if (signalExisting && OperatingSystem.IsWindows()) SignalExistingInstance();
				mutex.Dispose();
				return null;
			}
		}

		if (!OperatingSystem.IsWindows()) return new SingleInstanceGuard(mutex, null, ownsMutex, onActivate);

		EventWaitHandle? activationEvent = null;
		try
		{
			activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
			return new SingleInstanceGuard(mutex, activationEvent, ownsMutex, onActivate);
		}
		catch
		{
			activationEvent?.Dispose();
			if (ownsMutex)
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
			mutex.Dispose();
			throw;
		}
	}

	[SupportedOSPlatform("windows")]
	private static void SignalExistingInstance()
	{
		// Mutex 已经建立而事件监听线程可能尚未创建，短暂重试覆盖这个启动竞态。
		for (int attempt = 0; attempt < 10; attempt++)
		{
			try
			{
				using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
				activationEvent.Set();
				return;
			}
			catch (WaitHandleCannotBeOpenedException) when (attempt < 9)
			{
				Thread.Sleep(50);
			}
			catch (UnauthorizedAccessException) when (attempt < 9)
			{
				Thread.Sleep(50);
			}
			catch
			{
				return;
			}
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例激活监听失败不能阻断主进程。")]
	private void Listen()
	{
		if (_activationEvent is null) return;
		try
		{
			while (!_listenerCts.IsCancellationRequested)
			{
				if (!_activationEvent.WaitOne(250)) continue;
				if (_listenerCts.IsCancellationRequested) break;
				try { _onActivate(); } catch { }
			}
		}
		catch (ObjectDisposedException) when (_listenerCts.IsCancellationRequested)
		{
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例退出清理失败不能阻断应用退出。")]
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_listenerCts.Cancel();
		try { _activationEvent?.Set(); } catch { }
		if (_listener is not null && _listener != Thread.CurrentThread) _listener.Join(1000);
		_activationEvent?.Dispose();
		_listenerCts.Dispose();
		if (_ownsMutex)
		{
			try { _mutex?.ReleaseMutex(); } catch { }
		}
		_mutex?.Dispose();
		_ownsMutex = false;
	}
}
