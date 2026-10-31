using System.Security;
using Nori.Core.Notifications;
using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

internal readonly record struct LinuxNotificationSignal(uint Id, string? Action);

internal interface ILinuxApprovalNotification : IDisposable
{
	bool SupportsActions { get; }
	Task<uint> SendAsync(ApprovalNotice notice, bool english, Action<LinuxNotificationSignal> signal, CancellationToken cancellationToken);
	Task CloseAsync(uint id, CancellationToken cancellationToken);
}

/// <summary>
/// 每条通知持有自己的连接，并固定首次应答的服务唯一名称；桌面服务重启后不会误撤销复用的编号。
/// </summary>
internal sealed class LinuxApprovalNotification : ILinuxApprovalNotification
{
	/// <summary>与 scripts/linux-desktop.sh 注册的桌面入口同名，通知服务据此把通知归到 Nori。</summary>
	internal const string DesktopEntryId = "cn.erhio.noriDesktopPet";
	private const string Service = "org.freedesktop.Notifications";
	private const string Path = "/org/freedesktop/Notifications";
	private readonly DBusConnection _connection;
	private readonly string _icon;
	private readonly List<IDisposable> _subscriptions = [];
	private string? _owner;
	public bool SupportsActions { get; private set; }

	/// <param name="address">会话总线地址。</param>
	/// <param name="iconPath">发布包内图标的绝对路径；为空时不指定图标，由桌面入口或服务决定。</param>
	internal LinuxApprovalNotification(string? address, string? iconPath)
	{
		if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("会话 D-Bus 不可用");
		_connection = new DBusConnection(address);
		_icon = iconPath ?? "";
	}

	public async Task<uint> SendAsync(ApprovalNotice notice, bool english, Action<LinuxNotificationSignal> signal, CancellationToken cancellationToken)
	{
		await _connection.ConnectAsync().AsTask().WaitObservedAsync(cancellationToken).ConfigureAwait(false);
		// 首个请求发往知名名称并允许总线按需启动通知服务；回包的发送方就是实际应答的服务实例。
		(string? owner, string[] capabilities) = await _connection.CallMethodAsync(CapabilitiesRequest(),
				static (message, _) => (message.SenderAsString, message.GetBodyReader().ReadArrayOfString()))
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrEmpty(owner)) throw new InvalidOperationException("通知服务回包缺少发送方");
		_owner = owner;
		SupportsActions = capabilities.Contains("actions");
		foreach (string member in new[] { "ActionInvoked", "NotificationClosed" })
		{
			_subscriptions.Add(await _connection.AddMatchAsync(new MatchRule
			{
				Type = MessageType.Signal, Sender = _owner, Path = Path, Interface = Service, Member = member,
			}, (message, _) =>
			{
				if (message.SenderAsString != _owner) return default(LinuxNotificationSignal);
				var reader = message.GetBodyReader();
				uint id = reader.ReadUInt32();
				return new LinuxNotificationSignal(id, message.MemberAsString == "ActionInvoked" ? reader.ReadString() : null);
			}, (Notification<LinuxNotificationSignal> notification) =>
			{
				if (notification.HasValue) signal(notification.Value);
			}, emitOnCapturedContext: false).AsTask().WaitObservedAsync(cancellationToken).ConfigureAwait(false));
		}
		return await _connection.CallMethodAsync(NotifyRequest(notice, english, capabilities), static (message, _) => message.GetBodyReader().ReadUInt32())
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task CloseAsync(uint id, CancellationToken cancellationToken) =>
		_connection.CallMethodAsync(Request("CloseNotification", id)).WaitObservedAsync(cancellationToken);

	private MessageBuffer CapabilitiesRequest()
	{
		using var writer = _connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: Service, member: "GetCapabilities");
		return writer.CreateMessage();
	}

	/// <summary>后续请求只发给首次应答的实例，且不再触发自动启动。</summary>
	private MessageBuffer Request(string member, uint? id = null)
	{
		using var writer = _connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: _owner!, path: Path, @interface: Service, member: member,
			signature: id.HasValue ? "u" : "", flags: MessageFlags.NoAutoStart);
		if (id.HasValue) writer.WriteUInt32(id.Value);
		return writer.CreateMessage();
	}

	internal static (string Summary, string Body, string[] Actions) Format(ApprovalNotice notice, bool english, string[] capabilities)
	{
		string tool = notice.ToolName.Replace('\r', ' ').Replace('\n', ' ');
		if (tool.Length > ApprovalToastXml.MaxSummaryLength) tool = tool[..ApprovalToastXml.MaxSummaryLength] + "…";
		if (capabilities.Contains("body-markup")) tool = SecurityElement.Escape(tool) ?? "";
		string body = english ? $"Tool: {tool}\nOpen Nori to review this request." : $"工具：{tool}\n请打开 Nori 主界面确认此请求。";
		string[] actions = capabilities.Contains("actions") ?
		[
			"default", english ? "Open Nori" : "打开 Nori",
			ToastActivation.Encode(ToastAction.Allow, notice.RequestId), english ? "Allow" : "允许执行",
			ToastActivation.Encode(ToastAction.Deny, notice.RequestId), english ? "Deny" : "拒绝",
		] : [];
		return (english ? "Nori · Approval required" : "Nori · 需要授权", body, actions);
	}

	private MessageBuffer NotifyRequest(ApprovalNotice notice, bool english, string[] capabilities)
	{
		var content = Format(notice, english, capabilities);
		using var writer = _connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: _owner!, path: Path, @interface: Service, member: "Notify",
			signature: "susssasa{sv}i", flags: MessageFlags.NoAutoStart);
		writer.WriteString("Nori");
		writer.WriteUInt32(0);
		writer.WriteString(_icon);
		writer.WriteString(content.Summary);
		writer.WriteString(content.Body);
		writer.WriteArray(content.Actions);
		// desktop-entry 让通知服务按已注册的桌面入口归属应用名称、图标与通知设置。
		writer.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = VariantValue.String(DesktopEntryId) });
		// 请求有限展示时间，减少服务在回包前断开时失去编号的残留通知。
		writer.WriteInt32(60000);
		return writer.CreateMessage();
	}

	public void Dispose()
	{
		foreach (IDisposable subscription in _subscriptions) subscription.Dispose();
		_connection.Dispose();
	}
}
