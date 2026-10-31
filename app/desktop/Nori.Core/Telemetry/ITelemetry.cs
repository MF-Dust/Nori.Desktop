namespace Nori.Core.Telemetry;

/// <summary>
/// 应用遥测抽象。
///
/// Core 只依赖这个最小接口, 不依赖具体的 Sentry SDK, 这样配置/异常边界可以在纯逻辑测试中运行。
/// </summary>
public interface ITelemetry : IDisposable
{
	/// <summary>当前构建是否注入了遥测端点。</summary>
	bool IsAvailable { get; }

	/// <summary>当前是否真的启用了远程遥测。</summary>
	bool IsEnabled { get; }

	/// <summary>按用户开关重新启用或关闭遥测。</summary>
	void Configure(bool enabled);

	/// <summary>
	/// 捕获一个已经脱敏的异常边界。
	///
	/// tags 只接受白名单键值(见 TelemetrySanitizer.NormalizeTags), 值会被归一化,
	/// 白名单外的键在发送边界丢弃, 避免用户输入进入遥测标签。
	/// </summary>
	void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false, IReadOnlyDictionary<string, string>? tags = null);

	/// <summary>
	/// 以 warning 级别捕获已处理且可恢复的异常，例如 GPU 设备丢失后的自动重建。
	/// 默认实现退回普通的已处理异常，未区分级别的遥测实现无需改动。
	/// </summary>
	void CaptureWarning(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null) =>
		CaptureException(exception, operation, handled: true, terminal: false, tags);

	/// <summary>开始一个只包含固定操作名的性能事务。</summary>
	ITelemetryTransaction StartTransaction(string operation);

	/// <summary>在退出前有限等待待发送事件。</summary>
	Task FlushAsync(TimeSpan timeout);
}

/// <summary>性能事务的最小跨层句柄。</summary>
public interface ITelemetryTransaction : IDisposable
{
}

/// <summary>
/// 无遥测实现。
///
/// 无 DSN、本地开发和用户关闭开关时都使用它, 不创建网络客户端也不抛异常。
/// </summary>
public sealed class NoopTelemetry : ITelemetry
{
	private sealed class NoopTransaction : ITelemetryTransaction
	{
		public static readonly NoopTransaction Instance = new();

		public void Dispose()
		{
		}
	}

	public static readonly NoopTelemetry Instance = new();

	private NoopTelemetry()
	{
	}

	public bool IsAvailable => false;

	public bool IsEnabled => false;

	public void Configure(bool enabled)
	{
	}

	public void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false, IReadOnlyDictionary<string, string>? tags = null)
	{
	}

	public ITelemetryTransaction StartTransaction(string operation) => NoopTransaction.Instance;

	public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;

	public void Dispose()
	{
	}
}
