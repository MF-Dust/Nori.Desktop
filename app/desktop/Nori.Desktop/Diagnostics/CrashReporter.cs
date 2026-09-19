using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Security;
using Nori.Core.Telemetry;

namespace Nori.Desktop.Diagnostics;

/// <summary>
/// 全局异常兜底与崩溃窗口
///
/// 参考 ClassIsland 的 DiagnosticService / CrashWindow 模式, 三层兜底:
/// - <c>Dispatcher.UIThread.UnhandledException</c>: 记日志后弹崩溃窗口, 进程继续运行 (关窗即恢复);
/// - <c>AppDomain.UnhandledException</c>: 尽力记日志; 若 IsTerminating 则阻塞展示崩溃窗后再退出,
///   否则进程会在处理器返回时直接终止, 窗口一闪而过;
/// - <c>TaskScheduler.UnobservedTaskException</c>: SetObserved + 记日志, 不弹窗 ——
///   这类异常在 GC 时才浮出, 多为过期的后台任务失败, 弹致命窗过于惊吓 (与 ClassIsland 的有意偏离).
///
/// 崩溃窗用原生 Avalonia 构建: WebView2 可能正是故障源, 不能依赖它来显示错误.
/// </summary>
public static class CrashReporter
{
	private static FileLogger? _logger;
	private static ITelemetry _telemetry = NoopTelemetry.Instance;
	private static IClassicDesktopStyleApplicationLifetime? _lifetime;
	private static Window? _crashWindow;

	/// <summary>
	/// 注册域级兜底. 必须在 Avalonia 启动前调用 (Program.Main 里), 越早覆盖面越大.
	/// </summary>
	public static void RegisterDomainHandler()
	{
		AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
	}

	/// <summary>
	/// 注册 UI 线程与任务级兜底, 在框架初始化完成时调用一次.
	/// </summary>
	public static void Register(IClassicDesktopStyleApplicationLifetime lifetime)
	{
		if (_lifetime is not null) return; // 防止重复注册
		_lifetime = lifetime;
		Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
	}

	/// <summary>
	/// 挂接应用共用日志器. 挂接前的兜底日志会临时 new 一个 FileLogger 尽力落盘.
	/// </summary>
	public static void AttachLogger(FileLogger logger) => _logger = logger;

	/// <summary>挂接遥测器; 未挂接时使用空实现。</summary>
	public static void AttachTelemetry(ITelemetry telemetry) => _telemetry = telemetry ?? NoopTelemetry.Instance;

	/// <summary>记录 Avalonia 启动前的异常，供原生启动错误提示复用脱敏日志。</summary>
	public static void LogEarlyStartupFailure(string title, Exception exception, string? logDirectory = null)
	{
		string message = $"{SensitiveDataRedactor.Redact(title)}: {SensitiveDataRedactor.ExceptionSummary(exception)}";
		if (!string.IsNullOrWhiteSpace(logDirectory))
		{
			try { new FileLogger(logDirectory).Write(LogSource.Backend, "error", message); return; }
			catch { } // NOSONAR -- 关闭或降级阶段需继续完成后续清理，单项失败不能阻断流程
		}
		// 存储 marker 提交前不得创建 data 子目录；此时只保留控制台诊断。
		try { Console.Error.WriteLine(message); } catch { } // NOSONAR -- 关闭或降级阶段需继续完成后续清理，单项失败不能阻断流程
	}

	/// <summary>
	/// 安全的 fire-and-forget: 后台任务失败只记日志, 不崩进程也不弹窗.
	/// 取代裸的 <c>_ = SomeAsync()</c>, 让异常在发生当下就有上下文地落盘,
	/// 而不是拖到 GC 时变成一条没有时间线的 UnobservedTaskException.
	/// </summary>
	public static async void Forget(Task task, string what) // NOSONAR -- 这是受控的 UI 或后台 fire-and-forget 入口，内部已观察异常
	{
		try
		{
			await task;
		}
		catch (Exception exception)
		{
			_telemetry.CaptureException(exception, "background_task");
			WriteLogSafe($"{what} 失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
		}
	}

	/// <summary>
	/// 上报异常并按严重程度处理
	/// </summary>
	/// <param name="exception">异常</param>
	/// <param name="critical">true 表示启动期致命失败: 关闭崩溃窗即退出 (退出码 1);
	/// false 表示运行期兜底: 关闭窗口可继续运行</param>
	public static void Report(Exception exception, bool critical = false)
	{
		_telemetry.CaptureException(exception, critical ? "startup_failure" : "unhandled_exception",
			handled: false, terminal: critical, tags: LoadFailureTags(exception));
		WriteLogSafe(critical
			? $"致命异常: {SensitiveDataRedactor.ExceptionSummary(exception)}"
			: $"未处理异常: {SensitiveDataRedactor.ExceptionSummary(exception)}");

		// Avalonia 还没起来 (极早期启动失败): 无法展示任何窗口, 记完日志只能退出
		if (Application.Current is null || _lifetime is null)
		{
			if (critical)
			{
				FlushTelemetrySafe();
				ExitProcess(1);
			}
			return;
		}

		if (Dispatcher.UIThread.CheckAccess())
		{
			// UI 线程上非阻塞展示: 窗口保持打开, 用户自行决定复制/重启/退出或关闭继续
			_ = ShowCrashWindowAsync(critical, BuildReport(exception), friendlyText: null);
			return;
		}

		// 后台线程: 阻塞到用户处理完崩溃窗再返回。
		// 域级 IsTerminating 场景下进程会在处理器返回后立刻终止, 不等的话窗口来不及显示
		try
		{
			// InvokeAsync(Func<Task>) 返回的 Task 在内层异步操作 (含等窗) 完成时才完成
			Dispatcher.UIThread.InvokeAsync(() => ShowCrashWindowAsync(critical, BuildReport(exception), null))
				.Wait(TimeSpan.FromSeconds(5));
		}
		catch (Exception failure)
		{
			WriteLogSafe($"崩溃窗口展示失败: {failure}");
		}
		finally
		{
			if (critical)
			{
				FlushTelemetrySafe();
				ExitProcess(1);
			}
		}
	}

	/// <summary>
	/// 启动期致命错误提示 (无异常对象时的 Report(critical: true) 等价形式), 关闭窗口即退出码 1
	/// </summary>
	/// <param name="title">错误标题</param>
	/// <param name="message">给用户看的中文说明</param>
	public static void ReportStartupFatal(string title, string message)
	{
		_telemetry.CaptureException(new InvalidOperationException(title), "startup_fatal", handled: false, terminal: true);
		WriteLogSafe($"启动失败: {SensitiveDataRedactor.Redact(title)}: {SensitiveDataRedactor.Redact(message)}");

		if (Application.Current is null || _lifetime is null)
		{
			FlushTelemetrySafe();
			ExitProcess(1);
			return;
		}

		StringBuilder report = new();
		report.AppendLine(title);
		report.AppendLine(message);
		AppendEnvironmentInfo(report);

		if (Dispatcher.UIThread.CheckAccess())
		{
			_ = ShowCrashWindowAsync(critical: true, report.ToString(), title);
			return;
		}

		try
		{
			Dispatcher.UIThread.InvokeAsync(() => ShowCrashWindowAsync(critical: true, report.ToString(), title))
				.Wait(TimeSpan.FromSeconds(5));
		}
		catch (Exception failure)
		{
			WriteLogSafe($"崩溃窗口展示失败: {failure}");
		}
		finally
		{
			FlushTelemetrySafe();
			ExitProcess(1);
		}
	}

	// ---- 内部实现 ----

	/// <summary>
	/// 加载类失败的安全诊断标签 (NORI-1X / NORI-24): 只含 HRESULT、程序集文件名与类型名,
	/// 不上传 FileLoadException.FileName 的完整路径; 非加载类失败返回 null 不加标签。
	/// </summary>
	internal static IReadOnlyDictionary<string, string>? LoadFailureTags(Exception exception)
	{
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is FileLoadException fileLoad)
			{
				Dictionary<string, string> tags = new()
				{
					["exception_kind"] = "file_load",
					["hresult"] = $"0x{current.HResult:X8}",
				};
				if (!string.IsNullOrWhiteSpace(fileLoad.FileName)) tags["assembly"] = AssemblyFileName(fileLoad.FileName);
				return tags;
			}
			if (current is TypeLoadException typeLoad)
			{
				Dictionary<string, string> tags = new()
				{
					["exception_kind"] = "type_load",
					["hresult"] = $"0x{current.HResult:X8}",
				};
				if (!string.IsNullOrWhiteSpace(typeLoad.TypeName)) tags["type_name"] = typeLoad.TypeName!;
				return tags;
			}
		}
		return null;
	}

	/// <summary>
	/// 取程序集文件名并保证剥掉全部目录成分: FileName 可能带 \ 或 / 分隔符,
	/// Path.GetFileName 在 Linux 上不认反斜杠, 先统一替换避免目录部分泄漏进遥测。
	/// </summary>
	private static string AssemblyFileName(string fileName) =>
		Path.GetFileName(fileName.Replace('\\', '/'));

	private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
	{
		if (eventArgs.ExceptionObject is not Exception exception)
		{
			WriteLogSafe($"非 Exception 对象导致的域级失败: {SensitiveDataRedactor.ExceptionType(null)}");
			if (eventArgs.IsTerminating) ExitProcess(1);
			return;
		}
		try
		{
			Report(exception, eventArgs.IsTerminating);
		}
		catch // NOSONAR -- 关闭或降级阶段需继续完成后续清理，单项失败不能阻断流程
		{
			// 兜底自身都失败了, 只能放弃: 进程即将终止或已不可救
			if (eventArgs.IsTerminating) ExitProcess(1);
		}
	}

	private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
	{
		try
		{
			if (IsTransientWebViewFocusException(e.Exception))
			{
				WriteLogSafe($"忽略 WebView2 聚焦竞态: {SensitiveDataRedactor.ExceptionSummary(e.Exception)}");
				e.Handled = true;
				return;
			}
			Report(e.Exception, critical: false);
			e.Handled = true; // 兜底成功, 进程继续运行
		}
		catch (Exception reporterFailure)
		{
			// 崩溃报告流程自身出错时不吞: 保持 Handled=false 让异常走域级处理器的退出路径
			WriteLogSafe($"崩溃报告流程失败: {SensitiveDataRedactor.ExceptionSummary(reporterFailure)}");
		}
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		e.SetObserved(); // 已记录即视为已观察, 避免反复触发
		_telemetry.CaptureException(e.Exception, "unobserved_task");
		WriteLogSafe($"未观察的任务异常 (延迟至 GC 才暴露): {SensitiveDataRedactor.ExceptionSummary(e.Exception)}");
	}

	/// <summary>
	/// 构建完整诊断块: 版本 / 系统 / 时间 + 异常类型与堆栈 (迷你版 ClassIsland GetDiagnosticInfo)
	/// </summary>
	private static string BuildReport(Exception exception)
	{
		StringBuilder report = new();
		report.AppendLine("程序运行中发生了未处理的异常。");
		AppendEnvironmentInfo(report);
		report.AppendLine(new string('=', 40));
		report.AppendLine($"异常类型: {SensitiveDataRedactor.ExceptionType(exception)}");
		if (!string.IsNullOrWhiteSpace(exception.StackTrace))
			report.AppendLine(SensitiveDataRedactor.Redact(exception.StackTrace));
		return report.ToString();
	}

	private static void AppendEnvironmentInfo(StringBuilder report)
	{
		string version = Nori.Core.ProductVersion.Current;
		report.AppendLine($"Nori v{version}");
		report.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
		report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
	}

	/// <summary>
	/// 展示崩溃窗口并等待其关闭. 必须在 UI 线程调用; 返回的任务在窗口关闭时完成.
	/// 已有崩溃窗打开时不重复弹 (防重入).
	/// </summary>
	/// <param name="critical">致命路径: 关窗即退出码 1</param>
	/// <param name="details">可复制的完整诊断块</param>
	/// <param name="friendlyText">标题下方的友好说明; 为空时用默认文案</param>
	private static Task ShowCrashWindowAsync(bool critical, string details, string? friendlyText)
	{
		if (_crashWindow is not null)
		{
			WriteLogSafe("已有崩溃窗口打开, 忽略重复上报");
			return Task.CompletedTask;
		}

		TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		TextBox detailsBox = new()
		{
			Text = details,
			IsReadOnly = true,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			FontFamily = new FontFamily("Consolas, Courier New, monospace"),
			FontSize = 12,
		};

		TextBlock headline = new()
		{
			Text = "Nori 遇到了问题",
			FontSize = 18,
			FontWeight = FontWeight.Bold,
		};

		TextBlock summary = new()
		{
			Text = friendlyText ?? "程序运行中发生了未处理的错误, 完整信息如下。您可以复制错误信息用于反馈问题, 或重启/退出应用。\n直接关闭本窗口可继续运行。",
			TextWrapping = TextWrapping.Wrap,
			Opacity = 0.85,
		};

		Button copyButton = null!;
		copyButton = CreateButton("复制错误信息", async (_, _) =>
		{
			try
			{
				// 剪贴板在 TopLevel 上取, 不挂在按钮自身上
				IClipboard? clipboard = _crashWindow?.Clipboard;
				if (clipboard is not null)
				{
					await clipboard.SetTextAsync(details);
					copyButton.Content = "已复制";
				}
			}
			catch (Exception failure)
			{
				// 剪贴板可能被其他进程占用, 不影响其余按钮
				WriteLogSafe($"复制错误信息失败: {failure.Message}");
			}
		});

		// resolved 标记: 用户点了"重启/退出"后, Closed 里就不再走致命路径的退出逻辑
		bool resolved = false;

		Button restartButton = CreateButton("重启应用", (_, _) =>
		{
			try
			{
				string launcher = ResolveTrustedLauncher();
				ProcessStartInfo startInfo = new(launcher)
				{
					UseShellExecute = false,
					WorkingDirectory = ResolveTrustedPackageRoot(),
				};
				startInfo.ArgumentList.Add("--launcher-wait-pid");
				startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
				long startTicks;
				try { startTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
				catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					throw new InvalidOperationException("无法读取崩溃进程身份, 已拒绝无身份重启", failure);
				}
				startInfo.ArgumentList.Add("--launcher-wait-start-ticks");
				startInfo.ArgumentList.Add(startTicks.ToString());
				// 启动器与包根均由 ResolveTrusted* 完成物理路径和边界验证。
				using Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("启动器未返回进程"); // nosemgrep
				// 启动器通常会持续等待新宿主；若它在短时间内带错误退出，不能关闭当前错误窗口。
				if (child.WaitForExit(750) && child.ExitCode != 0)
					throw new InvalidOperationException($"启动器退出码 {child.ExitCode}");
				resolved = true;
				ShutdownSafely(0);
			}
			catch (Exception failure)
			{
				WriteLogSafe($"重启应用失败: {SensitiveDataRedactor.ExceptionSummary(failure)}");
				summary.Text = $"重启失败: {SensitiveDataRedactor.ExceptionSummary(failure)}。请手动退出或复制错误信息。";
			}
		});

		Button exitButton = CreateButton("退出应用", (_, _) =>
		{
			resolved = true;
			ShutdownSafely(1);
		});

		Grid buttonRow = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
		StackPanel buttons = new() { Spacing = 8, Orientation = Orientation.Horizontal };
		buttons.Children.Add(copyButton);
		buttons.Children.Add(restartButton);
		buttons.Children.Add(exitButton);
		Grid.SetColumn(buttons, 1);
		buttonRow.Children.Add(buttons);

		Grid root = new()
		{
			Margin = new Thickness(20),
			RowSpacing = 12,
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(new GridLength(1, GridUnitType.Star)),
				new RowDefinition(GridLength.Auto),
			},
		};
		root.Children.Add(headline);
		Grid.SetRow(summary, 1);
		root.Children.Add(summary);
		Grid.SetRow(detailsBox, 2);
		root.Children.Add(detailsBox);
		Grid.SetRow(buttonRow, 3);
		root.Children.Add(buttonRow);

		Window window = new()
		{
			Title = "Nori 遇到了问题",
			Width = 640,
			Height = 480,
			MinWidth = 460,
			MinHeight = 320,
			CanResize = true,
			Topmost = true,
			WindowStartupLocation = WindowStartupLocation.CenterScreen,
			Content = root,
		};
		window.Closed += (_, _) =>
		{
			_crashWindow = null;
			closed.TrySetResult();
			// 启动期致命路径保留原 ShowFatal 的"关窗即退出"语义; 用户已点过重启/退出则不再干预
			if (critical && !resolved)
			{
				FlushTelemetrySafe();
				ShutdownSafely(1);
			}
		};

		_crashWindow = window;
		try
		{
			window.Show();
			window.Activate();
		}
		catch (InvalidOperationException exception)
		{
			_crashWindow = null;
			closed.TrySetResult();
			WriteLogSafe($"崩溃窗口无法显示: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			if (critical) ShutdownSafely(1);
		}
		return closed.Task;
	}

	private static Button CreateButton(string label, EventHandler<RoutedEventArgs> onClick)
	{
		Button button = new() { Content = label, Padding = new Thickness(14, 6), MinWidth = 96 };
		button.Click += onClick;
		return button;
	}

	/// <summary>
	/// 兜底专用写日志: 日志器不可用时尽力自建一个, 再失败也只能放弃
	/// </summary>
	private static void FlushTelemetrySafe()
	{
		try
		{
			_telemetry.FlushAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
		}
		catch
		{
		}
	}

	private static void ExitProcess(int code)
	{
		if (_lifetime is not null)
		{
			ShutdownSafely(code);
			return;
		}
		Environment.Exit(code);
	}

	private static string ResolveTrustedPackageRoot()
	{
		string? value = Environment.GetEnvironmentVariable("NORI_PACKAGE_ROOT");
		string? deploymentValue = Environment.GetEnvironmentVariable("NORI_DEPLOYMENT_ROOT");
		string? launcherValue = Environment.GetEnvironmentVariable("NORI_LAUNCHER_PATH");
		string? executableValue = Environment.GetEnvironmentVariable("NORI_EXECUTABLE_PATH");
		if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(deploymentValue) || string.IsNullOrWhiteSpace(launcherValue) || string.IsNullOrWhiteSpace(executableValue))
			throw new InvalidOperationException("缺少受信任的发布启动环境");
		string root = Path.GetFullPath(value);
		string deployment = Path.GetFullPath(deploymentValue);
		string launcher = Path.GetFullPath(launcherValue);
		string executable = Path.GetFullPath(executableValue);
		string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
		string process = Path.GetFullPath(Environment.ProcessPath ?? "");
		EnsureCanonical(root, directory: true);
		EnsureCanonical(deployment, directory: true);
		EnsureCanonical(launcher, directory: false);
		EnsureCanonical(executable, directory: false);
		EnsureCanonical(baseDirectory, directory: true);
		EnsureCanonical(process, directory: false);
		if (!IsContained(deployment, root) || !string.Equals(Path.GetDirectoryName(deployment)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison)
			|| !IsContained(baseDirectory, deployment) || !IsContained(process, deployment) || !string.Equals(executable, process, PathComparison))
			throw new InvalidOperationException("当前宿主不属于受信任的发布槽");
		string expectedLauncher = OperatingSystem.IsMacOS()
			? Path.Combine(root, "Nori.app", "Contents", "MacOS", "Nori")
			: Path.Combine(root, OperatingSystem.IsWindows() ? "Nori.exe" : "Nori");
		if (!string.Equals(launcher, Path.GetFullPath(expectedLauncher), PathComparison))
			throw new InvalidOperationException("受信任的 launcher 路径不匹配");
		return root;
	}

	private static void EnsureCanonical(string path, bool directory)
	{
		if (string.IsNullOrWhiteSpace(path) || (directory ? !Directory.Exists(path) : !File.Exists(path)))
			throw new InvalidOperationException("受信任的启动路径不存在");
		string? current = path;
		while (current is not null && !string.IsNullOrEmpty(Path.GetPathRoot(current)))
		{
			if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException("受信任的启动路径包含 reparse point");
			string? parent = Path.GetDirectoryName(current);
			if (parent is null || string.Equals(parent, current, PathComparison)) break;
			current = parent;
		}
	}

	private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private static string ResolveTrustedLauncher()
	{
		string root = ResolveTrustedPackageRoot();
		string launcher = OperatingSystem.IsMacOS()
			? Path.Combine(root, "Nori.app", "Contents", "MacOS", "Nori")
			: Path.Combine(root, OperatingSystem.IsWindows() ? "Nori.exe" : "Nori");
		if (!IsContained(launcher, root) || !File.Exists(launcher) || (File.GetAttributes(launcher) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidOperationException("受信任的发布启动器不存在");
		return launcher;
	}

	private static bool IsContained(string path, string root)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
	}

	private static void ShutdownSafely(int code)
	{
		try { _lifetime?.Shutdown(code); }
		catch (InvalidOperationException) { }
	}

	private static bool IsTransientWebViewFocusException(Exception exception)
	{
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is COMException && current.HResult == unchecked((int)0x80070718)) return true;
		}
		return false;
	}

	private static void WriteLogSafe(string message)
	{
		try
		{
			if (_logger is null)
			{
				string root = ResolveTrustedPackageRoot();
				string dataDirectory = Path.Combine(root, "data");
				string markerPath = Path.Combine(dataDirectory, AppStoragePaths.MarkerFileName);
				if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0) return;
				string logDirectory = Path.Combine(dataDirectory, "diagnostics", "logs");
				if (!IsContained(logDirectory, root)) return;
				AppStoragePaths.EnsureNoReparsePoints(logDirectory, root);
				_logger = new FileLogger(logDirectory);
			}
			_logger.Write(LogSource.Backend, "error", SensitiveDataRedactor.Redact(message));
		}
		catch
		{
			// 日志系统自身不可用时只能放弃记录
		}
	}
}
