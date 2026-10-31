using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace Nori.Desktop.Startup;

/// <summary>
/// 跨平台单实例锁与第二实例激活信号。
///
/// 三平台都使用宿主命名 Mutex 保护启动与迁移；Windows 用命名事件，
/// Linux/macOS 用仅同用户可连接的命名管道唤醒第一个实例的 main 窗口。
/// 唤起通道只是便利功能：建立或监听失败时保留互斥并记录原因，不阻断启动。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3453", Justification = "实例必须由 TryAcquire 工厂创建以保证互斥体所有权状态一致。")]
internal sealed class SingleInstanceGuard : IDisposable
{
	private const string WindowsMutexName = @"Local\NoriDesktopPet.SingleInstance";
	private const string PortableMutexName = "NoriDesktopPet.SingleInstance";
	private const string ActivationEventName = @"Local\NoriDesktopPet.Activate";
	private const string PortableActivationSocketName = "NoriDesktopPet.Activate";
	private const int MaxUserNameLength = 64;

	private readonly Mutex? _mutex;
	private readonly EventWaitHandle? _activationEvent;
	private readonly NamedPipeServerStream? _activationPipe;
	private readonly CancellationTokenSource _listenerCts = new();
	private readonly Action _onActivate;
	private readonly Action<string, Exception>? _onFailure;
	private readonly Thread? _listener;
	private volatile bool _activationFailed;
	private bool _ownsMutex;
	private int _disposed;

	private SingleInstanceGuard(
		Mutex? mutex,
		EventWaitHandle? activationEvent,
		bool ownsMutex,
		Action onActivate,
		NamedPipeServerStream? activationPipe,
		Action<string, Exception>? onFailure)
	{
		_mutex = mutex;
		_activationEvent = activationEvent;
		_activationPipe = activationPipe;
		_ownsMutex = ownsMutex;
		_onActivate = onActivate;
		_onFailure = onFailure;

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

	/// <summary>唤起通道已建立且监听仍在运行。</summary>
	internal bool ActivationAvailable => !_activationFailed && (_activationEvent is not null || _activationPipe is not null);

	/// <summary>
	/// 尝试成为第一个实例。返回 null 表示已有实例；按 signalExisting 决定是否尝试唤起。
	/// </summary>
	public static SingleInstanceGuard? TryAcquire(Action onActivate, bool signalExisting = true, Action<string, Exception>? onFailure = null) =>
		TryAcquire(onActivate, signalExisting,
			OperatingSystem.IsWindows() ? WindowsMutexName : PortableMutexName,
			OperatingSystem.IsWindows()
				? ActivationEventName
				: PortableActivationPath(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Path.GetTempPath(), Environment.UserName),
			onFailure);

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例获取失败后的句柄清理不能覆盖原始启动错误。")]
	internal static SingleInstanceGuard? TryAcquire(
		Action onActivate,
		bool signalExisting,
		string mutexName,
		string activationName,
		Action<string, Exception>? onFailure = null)
	{
		ArgumentNullException.ThrowIfNull(onActivate);
		Mutex mutex = CreateMutex(mutexName, out bool createdNew);
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
			if (OperatingSystem.IsWindows()) activationEvent = TryCreateActivationEvent(activationName, onFailure);
			else activationPipe = TryCreateActivationPipe(activationName, onFailure);
			return new SingleInstanceGuard(mutex, activationEvent, ownsMutex, onActivate, activationPipe, onFailure);
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

	/// <summary>
	/// Linux/macOS 唤起 socket 的位置。
	///
	/// 相对管道名会落到共享临时目录（Linux 通常是 /tmp），其他用户的同名文件删不掉就会绑定失败。
	/// 因此优先使用仅本用户可访问的 $XDG_RUNTIME_DIR；缺失时退回临时目录并按用户名区分。
	/// </summary>
	internal static string PortableActivationPath(string? runtimeDirectory, string tempDirectory, string userName)
	{
		if (!string.IsNullOrEmpty(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory) && Directory.Exists(runtimeDirectory))
			return Path.Combine(runtimeDirectory, PortableActivationSocketName);
		return Path.Combine(tempDirectory, $"{PortableActivationSocketName}.{SafeUserName(userName)}");
	}

	private static string SafeUserName(string userName)
	{
		StringBuilder safe = new(Math.Min(userName.Length, MaxUserNameLength));
		foreach (char character in userName)
		{
			if (safe.Length == MaxUserNameLength) break;
			safe.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_');
		}
		return safe.Length == 0 ? "user" : safe.ToString();
	}

	private static Mutex CreateMutex(string name, out bool createdNew)
	{
		if (OperatingSystem.IsWindows()) return new Mutex(true, name, out createdNew);
		// .NET 在 Unix 上默认按 getsid 隔离命名 Mutex：不同终端、登录自启与菜单启动会各自拿到锁。
		return new Mutex(true, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);
	}

	[SupportedOSPlatform("windows")]
	private static EventWaitHandle? TryCreateActivationEvent(string activationName, Action<string, Exception>? onFailure)
	{
		try
		{
			return new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
		}
		catch (Exception exception) when (IsActivationChannelFailure(exception))
		{
			ReportFailure(onFailure, "无法建立再次启动唤起通道", exception);
			return null;
		}
	}

	private static NamedPipeServerStream? TryCreateActivationPipe(string activationName, Action<string, Exception>? onFailure)
	{
		try
		{
			return new NamedPipeServerStream(activationName, PipeDirection.In, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		}
		catch (Exception exception) when (IsActivationChannelFailure(exception))
		{
			ReportFailure(onFailure, "无法建立再次启动唤起通道", exception);
			return null;
		}
	}

	private static bool IsActivationChannelFailure(Exception exception) =>
		exception is IOException or SocketException or UnauthorizedAccessException or ArgumentException
			or PlatformNotSupportedException or WaitHandleCannotBeOpenedException;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "唤起失败记录本身失败时不能影响主进程。")]
	private static void ReportFailure(Action<string, Exception>? onFailure, string message, Exception exception)
	{
		Console.Error.WriteLine($"{message}：{exception.GetType().Name}");
		try { onFailure?.Invoke(message, exception); } catch { }
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
		catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException
			or SocketException or ArgumentException or PlatformNotSupportedException)
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
				Activate();
			}
		}
		catch (Exception exception)
		{
			StopListening(exception);
		}
	}

	private void Listen()
	{
		if (_activationEvent is null) return;
		try
		{
			while (!_listenerCts.IsCancellationRequested)
			{
				if (!_activationEvent.WaitOne(250)) continue;
				if (_listenerCts.IsCancellationRequested) break;
				Activate();
			}
		}
		catch (Exception exception)
		{
			StopListening(exception);
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "单实例激活回调失败不能阻断监听。")]
	private void Activate()
	{
		try { _onActivate(); }
		catch (Exception exception) { Console.Error.WriteLine($"唤起 Nori 主窗口失败：{exception.GetType().Name}"); }
	}

	/// <summary>后台线程的未处理异常会终止进程；退出时的取消与释放是正常路径，其余失败停用唤起并记录。</summary>
	private void StopListening(Exception exception)
	{
		if (_listenerCts.IsCancellationRequested) return;
		_activationFailed = true;
		ReportFailure(_onFailure, "再次启动唤起监听已停止", exception);
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
