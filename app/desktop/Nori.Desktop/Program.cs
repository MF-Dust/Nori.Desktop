using System;
using System.Runtime.InteropServices;
using Avalonia;
using Nori.Core;
using Nori.Core.Data;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Startup;

namespace Nori.Desktop;

/// <summary>
/// 进程入口
/// </summary>
internal static class Program
{
	private static int _activationPending;

	internal static StartupOptions? Options { get; private set; }
	internal static AppStoragePaths? StoragePaths { get; private set; }
	internal static StorageBootstrapResult? StorageMigration { get; private set; }

	internal static bool ConsumePendingActivation() => Interlocked.Exchange(ref _activationPending, 0) == 1;

	internal static string RuntimeRid()
	{
		string rid = RuntimeInformation.RuntimeIdentifier;
		if (rid.StartsWith("win-", StringComparison.Ordinal) || rid.StartsWith("linux-", StringComparison.Ordinal) || rid.StartsWith("osx-", StringComparison.Ordinal)) return rid;
		string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		string architecture = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => throw new InvalidOperationException("不支持的 CPU 架构"),
		};
		return $"{os}-{architecture}";
	}

	private static void ShowStartupError(string title, string message)
	{
		string safe = Nori.Core.Security.SensitiveDataRedactor.Redact(message);
		if (OperatingSystem.IsWindows())
		{
			MessageBox(nint.Zero, safe, title, 0x10);
			return;
		}
		if (OperatingSystem.IsMacOS())
		{
			try
			{
				System.Diagnostics.ProcessStartInfo alert = new("osascript") { UseShellExecute = false };
				alert.ArgumentList.Add("-e");
				alert.ArgumentList.Add($"display alert {AppleScriptString(title)} message {AppleScriptString(safe)}");
				using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(alert);
				if (process is null) throw new InvalidOperationException("无法显示启动错误");
				process.WaitForExit(5000);
				return;
			}
			catch { }
		}
		Console.Error.WriteLine($"{title}: {safe}");
	}

	private static string AppleScriptString(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\"";

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

	private static bool IsDevelopmentProcess() =>
		string.Equals(ProductVersion.Current, "Dev", StringComparison.Ordinal);

	private static void ActivateFirstInstance()
	{
		if (Application.Current is App app) app.ActivateMainWindow();
		else Interlocked.Exchange(ref _activationPending, 1);
	}

	private static string? SafeEarlyLogDirectory()
	{
		if (StoragePaths is not { } paths || !File.Exists(paths.MarkerPath)) return null;
		try
		{
			if ((File.GetAttributes(paths.MarkerPath) & FileAttributes.ReparsePoint) != 0) return null;
			AppStoragePaths.EnsureNoReparsePoints(paths.LogsDirectory, paths.PackageRoot);
			return paths.LogsDirectory;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			return null;
		}
	}

	[STAThread]
	public static void Main(string[] args)
	{
		if (!StartupOptions.TryParse(args, out StartupOptions? startup, out string parseError))
		{
			ShowStartupError("启动参数错误", parseError);
			Environment.ExitCode = 2;
			return;
		}

		Options = startup;
		bool safeMode = startup?.SafeMode == true;
		SmokeTestOptions? smokeTest = startup?.SmokeTest;
		try
		{
			StoragePaths = smokeTest is null
				? AppStoragePathResolver.Resolve()
				: new AppStoragePaths(smokeTest.Profile);
			if (smokeTest is not null)
			{
				SmokeTestRuntime.Configure(smokeTest);
			}
		}
		catch (Exception exception)
		{
			string summary = Nori.Core.Security.SensitiveDataRedactor.ExceptionSummary(exception);
			CrashReporter.LogEarlyStartupFailure("存储包根不可用", exception, SafeEarlyLogDirectory());
			ShowStartupError("存储包根不可用", summary);
			Environment.ExitCode = 2;
			return;
		}

		// 在 Avalonia 启动前就挂上域级兜底, 尽早覆盖启动期异常 (参考 ClassIsland Program.cs)
		CrashReporter.RegisterDomainHandler();
		// 冒烟使用隔离 profile，不能被用户正在运行的正式实例互斥量拦截。
		using SingleInstanceGuard? singleInstance = smokeTest is null
			? SingleInstanceGuard.TryAcquire(ActivateFirstInstance, signalExisting: !safeMode)
			: null;
		if (smokeTest is null && singleInstance is null)
		{
			if (safeMode)
			{
				Console.Error.WriteLine("安全模式无法启动: Nori 已有一个实例正在运行");
				Environment.ExitCode = 3;
			}
			return;
		}
		try
		{
			AppStoragePaths paths = StoragePaths ?? throw new InvalidOperationException("存储路径尚未初始化");
			bool development = IsDevelopmentProcess();
			string? legacy = development || smokeTest is not null ? null : LegacyDataPathResolver.Resolve();
			StorageMigration = StorageBootstrapper.Bootstrap(
				paths,
				ProductVersion.Current,
				RuntimeRid(),
				legacy,
				allowLegacyMigration: !development && smokeTest is null);
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}
		catch (Exception exception)
		{
			string summary = Nori.Core.Security.SensitiveDataRedactor.ExceptionSummary(exception);
			// marker 尚未提交时不能在 data 下创建日志目录，否则下一次启动会把它视为无 marker 脏数据。
			CrashReporter.LogEarlyStartupFailure("存储初始化失败", exception, SafeEarlyLogDirectory());
			ShowStartupError("存储初始化失败", summary);
			Environment.ExitCode = 1;
		}
	}

	/// <summary>
	/// Avalonia 配置 (设计器也会用到, 不要删)
	/// </summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UsePlatformDetect()
		.LogToTrace();
}
