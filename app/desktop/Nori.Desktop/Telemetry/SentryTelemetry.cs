using PlatformOperatingSystem = System.OperatingSystem;
using Nori.Core.Telemetry;
using Sentry;
using Sentry.Protocol;

namespace Nori.Desktop.Telemetry;

/// <summary>
/// Native Sentry 客户端。
///
/// 只有发布构建注入 Native DSN 且用户开关打开时才初始化 SDK。所有事件在 SDK 的
/// BeforeSend 边界再次清空请求、用户、面包屑和异常正文, 避免聊天/密钥从异常链路泄露。
/// </summary>
public sealed class SentryTelemetry : ITelemetry
{
	/// <summary>生产构建禁止故意崩溃测试, 防止调试入口进入正式包。</summary>
	public static bool IsProductionBuild => string.Equals(SentryBuildConfig.Environment, "production", StringComparison.OrdinalIgnoreCase);

	private readonly object _gate = new();
	private readonly string _dsn;
	private readonly string _release;
	private readonly string _environment;
	private IDisposable? _sdk;
	private bool _disposed;
	private bool _enabled;

	public SentryTelemetry(string? dsn, string? release, string? environment)
	{
		_dsn = dsn?.Trim() ?? string.Empty;
		_release = release?.Trim() ?? string.Empty;
		_environment = string.IsNullOrWhiteSpace(environment) ? "production" : environment.Trim();
	}

	public bool IsAvailable => _dsn.Length > 0;

	public bool IsEnabled
	{
		get
		{
			lock (_gate) return _enabled;
		}
	}

	/// <summary>按用户偏好开关 SDK。</summary>
	public void Configure(bool enabled)
	{
		lock (_gate)
		{
			if (_disposed) return;
			bool shouldEnable = enabled && IsAvailable;
			if (_enabled == shouldEnable) return;

			CloseLocked();
			if (!shouldEnable) return;

			try
			{
				_sdk = SentrySdk.Init(options => ConfigureOptions(options));
				_enabled = _sdk is not null;
			}
			catch
			{
				// 遥测不能影响应用启动与业务功能; 初始化失败保持空状态。
				_sdk = null;
				_enabled = false;
			}
		}
	}

	/// <summary>
	/// 测试观测缝: 在 BeforeSend 边界观察最终事件, 返回 null 即丢弃 (不会出网)。
	/// 仅测试代码允许设置; 生产路径保持 null。
	/// </summary>
	internal Func<SentryEvent, SentryEvent?>? TestBeforeSend { get; set; }

	public void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false, IReadOnlyDictionary<string, string>? tags = null)
	{
		if (exception is null) return;
		lock (_gate)
		{
			if (!_enabled || _disposed) return;
			string normalizedOperation = TelemetrySanitizer.NormalizeOperation(operation);
			IReadOnlyDictionary<string, string> safeTags = TelemetrySanitizer.NormalizeTags(tags);
			try
			{
				SentrySdk.CaptureException(exception, handled, terminal, scope =>
				{
					scope.SetTag("operation", normalizedOperation);
					foreach ((string key, string value) in safeTags) scope.SetTag(key, value);
				});
			}
			catch
			{
				// 上报失败不能递归进入崩溃兜底。
			}
		}
	}

	public ITelemetryTransaction StartTransaction(string operation)
	{
		lock (_gate)
		{
			if (!_enabled || _disposed) return NoopTransaction.Instance;
			try
			{
				ITransactionTracer transaction = SentrySdk.StartTransaction(
					TelemetrySanitizer.NormalizeOperation(operation),
					"nori.operation");
				return new TransactionHandle(transaction);
			}
			catch
			{
				return NoopTransaction.Instance;
			}
		}
	}

	public async Task FlushAsync(TimeSpan timeout)
	{
		try
		{
			bool enabled;
			lock (_gate) enabled = _enabled && !_disposed;
			if (enabled) await SentrySdk.FlushAsync(timeout).ConfigureAwait(false);
		}
		catch // NOSONAR -- 关闭或降级阶段需继续完成后续清理，单项失败不能阻断流程
		{
			// 退出阶段不再抛出遥测异常。
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			CloseLocked();
		}
	}

	private void ConfigureOptions(SentryOptions options)
	{
		options.Dsn = _dsn;
		options.Release = string.IsNullOrWhiteSpace(_release) ? null : _release;
		options.Environment = _environment;
		options.IsGlobalModeEnabled = true;
		options.SendDefaultPii = false;
		options.IsEnvironmentUser = false;
		options.SampleRate = 1.0f;
		options.TracesSampleRate = 0.25;
		options.ProfilesSampleRate = 0.0;
		options.EnableLogs = false;
		options.EnableMetrics = false;
		options.AutoSessionTracking = false;
		options.DisableAppDomainUnhandledExceptionCapture();
		options.DisableUnobservedTaskExceptionCapture();
		options.DisableAppDomainProcessExitFlush();
		options.MaxBreadcrumbs = 0;
		options.ShutdownTimeout = TimeSpan.FromSeconds(1);
		options.FlushTimeout = TimeSpan.FromSeconds(1);
		options.DefaultTags.Add("runtime", "native");
		options.DefaultTags.Add("os", OperatingSystemName());
		options.DefaultTags.Add("session_type", SessionType());
		options.TracePropagationTargets.Clear();
		options.SetBeforeSend(ScrubEvent);
		options.SetBeforeSendTransaction(ScrubTransaction);
		options.SetBeforeBreadcrumb(DropBreadcrumb);
	}

	private SentryEvent? ScrubEvent(SentryEvent current, SentryHint hint)
	{
		current.Request = null!;
		current.User = null!;
		current.Contexts?.Clear();
		if (current.Extra is IDictionary<string, object> extra) extra.Clear();
		current.Message = null!;
		current.ServerName = null!;
		current.TransactionName = TelemetrySanitizer.NormalizeOperation(current.TransactionName);
		PreserveSafeTags(current);
		current.SetTag("runtime", "native");
		current.SetTag("os", OperatingSystemName());
		current.SetTag("session_type", SessionType());
		if (current.SentryExceptions is not null)
		{
			foreach (SentryException exception in current.SentryExceptions)
			{
				exception.Value = TelemetrySanitizer.SanitizeExceptionValue(current.Exception);
				if (exception.Stacktrace?.Frames is { } frames)
				{
					foreach (SentryStackFrame frame in frames)
					{
						// 这里只清洗遥测帧字段，不执行任何命令。
						frame.FileName = ScrubPath(frame.FileName); // nosemgrep
						frame.AbsolutePath = null;
						frame.ContextLine = null;
					}
				}
			}
		}
		return TestBeforeSend is null ? current : TestBeforeSend(current);
	}

	/// <summary>
	/// 事件 tag 白名单: scope 合并进来的键里只保留固定安全键, 其余(可能含用户输入)丢弃。
	/// </summary>
	private static void PreserveSafeTags(SentryEvent current)
	{
		if (current.Tags is null || current.Tags.Count == 0) return;

		List<(string Key, string Value)> safe = [];
		foreach ((string rawKey, string rawValue) in current.Tags)
		{
			string key = TelemetrySanitizer.NormalizeTag(rawKey);
			if (key is "runtime" or "os" or "session_type") continue;
			string value = TelemetrySanitizer.NormalizeTag(rawValue);
			if (value.Length > 0) safe.Add((key, value));
		}
		foreach (string key in current.Tags.Keys.ToArray()) current.UnsetTag(key);
		foreach ((string key, string value) in safe) current.SetTag(key, value);
	}

	private Sentry.SentryTransaction? ScrubTransaction(Sentry.SentryTransaction current, SentryHint hint)
	{
		current.Request = null!;
		current.User = null!;
		current.Contexts?.Clear();
		if (current.Data is IDictionary<string, object> data) data.Clear();
		current.Description = null!;
		if (current.Tags is not null)
		{
			foreach (string key in current.Tags.Keys.ToArray()) current.UnsetTag(key);
		}
		current.SetTag("runtime", "native");
		current.SetTag("os", OperatingSystemName());
		current.SetTag("session_type", SessionType());
		return current;
	}

	private static Breadcrumb? DropBreadcrumb(Breadcrumb breadcrumb, SentryHint hint) => null;

	private static string? ScrubPath(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return value;
		string trimmed = value.Trim();
		if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.Contains("://", StringComparison.Ordinal))
			return "[path]";
		return trimmed.Length > 240 ? trimmed[..240] : trimmed;
	}

	private void CloseLocked()
	{
		_enabled = false;
		try
		{
			_sdk?.Dispose();
		}
		catch
		{
		}
		finally
		{
			_sdk = null;
		}
	}

	private static string OperatingSystemName() =>
		PlatformOperatingSystem.IsWindows() ? "windows" :
		PlatformOperatingSystem.IsMacOS() ? "macos" :
		PlatformOperatingSystem.IsLinux() ? "linux" : "unknown";

	private static string SessionType()
	{
		if (PlatformOperatingSystem.IsWindows()) return "windows";
		if (PlatformOperatingSystem.IsMacOS()) return "macos";
		string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant() ?? "";
		return session switch
		{
			"x11" => "x11",
			"wayland" => "wayland",
			_ => "unknown",
		};
	}

	private sealed class TransactionHandle(ITransactionTracer transaction) : ITelemetryTransaction
	{
		private int _finished;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _finished, 1) != 0) return;
			try
			{
				transaction.Finish();
			}
			catch
			{
			}
		}
	}

	private sealed class NoopTransaction : ITelemetryTransaction
	{
		public static readonly NoopTransaction Instance = new();

		public void Dispose()
		{
		}
	}
}
