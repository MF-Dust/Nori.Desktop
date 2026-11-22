using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>Wayland 截屏由系统选择画面；请求与响应绑定同一 Portal 服务实例。</summary>
internal static class LinuxScreenshotPortal
{
	private const string Service = "org.freedesktop.portal.Desktop";
	private const string Path = "/org/freedesktop/portal/desktop";
	private const string ScreenshotInterface = "org.freedesktop.portal.Screenshot";
	private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(2);

	internal static string? SessionBusAddress => LinuxNativeNotifier.HasSessionBus ? DBusAddress.Session : null;

	internal static Task<string> RequestFileAsync(CancellationToken cancellationToken)
	{
		string? address = SessionBusAddress;
		if (string.IsNullOrWhiteSpace(address))
			throw new InvalidOperationException("会话 D-Bus 不可用，无法启动截屏 Portal");
		return RequestFileAsync(address, cancellationToken);
	}

	internal static async Task<string> RequestFileAsync(string address, CancellationToken cancellationToken)
	{
		using DBusConnection connection = new(address);
		try
		{
			await connection.ConnectAsync().AsTask().WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
			// 允许服务按需启动，并使用回包中的唯一名称约束截图及其信号发送方。
			string owner = await connection.CallMethodAsync(VersionRequest(connection), static (message, _) =>
				message.SenderAsString ?? throw new InvalidOperationException("截屏 Portal 回包缺少发送方"))
				.WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
			string token = $"nori_{Guid.NewGuid():N}";
			string sender = connection.UniqueName![1..].Replace('.', '_');
			string requestPath = $"{Path}/request/{sender}/{token}";
			TaskCompletionSource<(uint Code, string? Uri)> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
			// Portal 可能立即应答；用 handle_token 算出请求路径，在发出调用前完成订阅。
			using IDisposable subscription = await connection.AddMatchAsync(new MatchRule
			{
				Type = MessageType.Signal, Sender = owner, Path = requestPath,
				Interface = "org.freedesktop.portal.Request", Member = "Response",
			}, static (message, _) =>
			{
				var reader = message.GetBodyReader();
				uint code = reader.ReadUInt32();
				if (code != 0) return (code, (string?)null);
				var results = reader.ReadDictionaryOfStringToVariantValue();
				return (code, results.TryGetValue("uri", out VariantValue uri) ? uri.GetString() : null);
			}, notification =>
			{
				if (notification.HasValue) response.TrySetResult(notification.Value);
				else if (notification.Exception is not null) response.TrySetResult((2, null));
			}, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed)
				.AsTask().WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);

			string actualPath = await connection.CallMethodAsync(ScreenshotRequest(connection, owner, token),
				static (message, _) => message.GetBodyReader().ReadObjectPathAsString())
				.WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
			if (actualPath != requestPath) throw new InvalidOperationException("截屏 Portal 返回了不匹配的请求路径");

			// 超时或取消时释放本次连接；Portal 会随调用方离线关闭未完成的请求。
			(uint code, string? uri) = await response.Task.WaitObservedAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
			if (code == 1) throw new InvalidOperationException("用户已取消系统截屏");
			if (code != 0) throw new InvalidOperationException("系统截屏失败或被拒绝");
			if (uri?.StartsWith("file://", StringComparison.OrdinalIgnoreCase) != true
				|| !Uri.TryCreate(uri, UriKind.Absolute, out Uri? file) || !file.IsFile || file.Host.Length != 0
				|| !System.IO.Path.IsPathFullyQualified(file.LocalPath))
				throw new InvalidOperationException("截屏 Portal 未返回本地截图文件");
			return file.LocalPath;
		}
		catch (DBusExceptionBase exception) { throw new InvalidOperationException("无法通过截屏 Portal 获取截图", exception); }
		catch (TimeoutException exception) { throw new InvalidOperationException("等待截屏 Portal 响应超时", exception); }
	}

	private static MessageBuffer VersionRequest(DBusConnection connection)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: "org.freedesktop.DBus.Properties",
			member: "Get", signature: "ss");
		writer.WriteString(ScreenshotInterface);
		writer.WriteString("version");
		return writer.CreateMessage();
	}

	private static MessageBuffer ScreenshotRequest(DBusConnection connection, string owner, string token)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: owner, path: Path, @interface: ScreenshotInterface,
			member: "Screenshot", signature: "sa{sv}");
		writer.WriteString("");
		writer.WriteDictionary(new Dictionary<string, VariantValue>
		{
			["handle_token"] = VariantValue.String(token),
			["interactive"] = VariantValue.Bool(true),
		});
		return writer.CreateMessage();
	}
}
