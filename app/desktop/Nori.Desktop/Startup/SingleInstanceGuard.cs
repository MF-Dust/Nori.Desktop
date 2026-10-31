using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Nori.Desktop.Startup;

/// <summary>
/// 跨平台单实例锁与第二实例激活信号。
///
/// 三平台都使用宿主命名 Mutex 保护启动与迁移；Windows 用命名事件，
/// Linux/macOS 用仅同用户可连接的命名管道唤醒第一个实例的 main 窗口。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3453", Justification = "实例必须由 TryAcquire 工厂创建以保证互斥体所有权状态一致。")]
internal sealed class SingleInstanceGuard : IDisposable
{
	private const string WindowsMutexName = @"Local\NoriDesktopPet.SingleInstance";
	private const string PortableMutexName = "NoriDesktopPet.SingleInstance";
	private const string ActivationEventName = @"Local\NoriDesktopPet.Activate";
	private const string PortableActivationPipeName = "NoriDesktopPet.Activate";

	private readonly Mutex? _mutex;
	private readonly EventWaitHandle? _activationEvent;
	private readonly NamedPipeServerStream? _activationPipe;
	private readonly CancellationTokenSource _listenerCts = new();
	private readonly Action _onActivate;
	private readonly Thread? _listener;
	private bool _ownsMutex;
	private int _disposed;

	private SingleInstanceGuard(
		Mutex? mutex,
		EventWaitHandle? activationEvent,
		bool ownsMutex,
		Action onActivate,
		NamedPipeServerStream? activationPipe = null)
	{
		_mutex = mutex;
		_activationEvent = activationEvent;
		_activationPipe = activationPipe;
		_ownsMutex = ownsMutex;
		_onActivate = onActivate;

		if (_activationEvent is not null || _activationPipe is not null)
		{
			_listener = new Thread(_activationPipe is null ? Listen : ListenPipe)
			{
				IsBackground = true,
				Name = "Nori second-instance listener",
			};
			_listener.Start();
		}
	}

	/// <summary>
	/// 尝试成为第一个实例。返回 null 表示已有实例；按 signalExisting 决定是否尝试唤起。
	/// </summary>
	public static SingleInstanceGuard? TryAcquire(Action onActivate, bool signalExisting = true) =>
		TryAcquire(onActivate, signalExisting,
			OperatingSystem.IsWindows() ? WindowsMutexName : PortableMutexName,
			OperatingSystem.IsWindows() ? ActivationEventName : PortableActivationPipeName);

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例获取失败后的句柄清理不能覆盖原始启动错误。")]
	internal static SingleInstanceGuard? TryAcquire(Action onActivate, bool signalExisting, string mutexName, string activationName)
	{
		ArgumentNullException.ThrowIfNull(onActivate);
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
				if (signalExisting)
				{
					if (OperatingSystem.IsWindows()) SignalExistingInstance(activationName);
					else SignalExistingPortableInstance(activationName);
				}
				mutex.Dispose();
				return null;
			}
		}

		EventWaitHandle? activationEvent = null;
		NamedPipeServerStream? activationPipe = null;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
				return new SingleInstanceGuard(mutex, activationEvent, ownsMutex, onActivate);
			}
			activationPipe = new NamedPipeServerStream(activationName, PipeDirection.In, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			return new SingleInstanceGuard(mutex, null, ownsMutex, onActivate, activationPipe);
		}
		catch
		{
			activationEvent?.Dispose();
			activationPipe?.Dispose();
			if (ownsMutex)
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
			mutex.Dispose();
			throw;
		}
	}

	[SupportedOSPlatform("windows")]
	private static void SignalExistingInstance(string activationName)
	{
		// Mutex 已经建立而事件监听线程可能尚未创建，短暂重试覆盖这个启动竞态。
		for (int attempt = 0; attempt < 10; attempt++)
		{
			try
			{
				using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(activationName);
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

	private static void SignalExistingPortableInstance(string activationName)
	{
		try
		{
			using NamedPipeClientStream pipe = new(".", activationName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
			// Connect 自带有界重试，覆盖 Mutex 已创建、管道尚未建立的启动空窗。
			pipe.Connect(500);
		}
		catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"无法唤起已有 Nori 实例：{exception.GetType().Name}");
		}
	}

	private void ListenPipe()
	{
		if (_activationPipe is null) return;
		try
		{
			while (!_listenerCts.IsCancellationRequested)
			{
				try
				{
					_activationPipe.WaitForConnectionAsync(_listenerCts.Token).GetAwaiter().GetResult();
				}
				catch (UnauthorizedAccessException)
				{
					// .NET 已关闭不同用户的连接；拒绝该连接后继续等待合法请求。
					Console.Error.WriteLine("已拒绝其他用户的 Nori 唤起请求");
					continue;
				}
				_activationPipe.Disconnect();
				if (_listenerCts.IsCancellationRequested) break;
				try { _onActivate(); }
				catch (Exception exception) { Console.Error.WriteLine($"唤起 Nori 主窗口失败：{exception.GetType().Name}"); }
			}
		}
		catch (OperationCanceledException) when (_listenerCts.IsCancellationRequested) { }
		catch (ObjectDisposedException) when (_listenerCts.IsCancellationRequested) { }
		catch (IOException exception)
		{
			Console.Error.WriteLine($"Nori 实例唤起监听失败：{exception.GetType().Name}");
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
		_activationPipe?.Dispose();
		_listenerCts.Dispose();
		if (_ownsMutex)
		{
			try { _mutex?.ReleaseMutex(); } catch { }
		}
		_mutex?.Dispose();
		_ownsMutex = false;
	}
}
