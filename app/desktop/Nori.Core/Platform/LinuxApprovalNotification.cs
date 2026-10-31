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

/// <summary>每条通知持有自己的连接和服务唯一名称，桌面服务重启后不会误撤销复用的编号。</summary>
internal sealed class LinuxApprovalNotification : ILinuxApprovalNotification
{
	private const string Service = "org.freedesktop.Notifications";
	private const string Path = "/org/freedesktop/Notifications";
	private readonly DBusConnection _connection = new(DBusAddress.Session!);
	private readonly List<IDisposable> _subscriptions = [];
	private string? _owner;
	public bool SupportsActions { get; private set; }

	public async Task<uint> SendAsync(ApprovalNotice notice, bool english, Action<LinuxNotificationSignal> signal, CancellationToken cancellationToken)
	{
		await _connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
		_owner = await _connection.CallMethodAsync(OwnerRequest(), static (message, _) => message.GetBodyReader().ReadString())
			.WaitAsync(cancellationToken).ConfigureAwait(false);
		string[] capabilities = await _connection.CallMethodAsync(Request("GetCapabilities"), static (message, _) => message.GetBodyReader().ReadArrayOfString())
			.WaitAsync(cancellationToken).ConfigureAwait(false);
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
			}, emitOnCapturedContext: false).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false));
		}
		return await _connection.CallMethodAsync(NotifyRequest(notice, english, capabilities), static (message, _) => message.GetBodyReader().ReadUInt32())
			.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task CloseAsync(uint id, CancellationToken cancellationToken) =>
		_connection.CallMethodAsync(Request("CloseNotification", id)).WaitAsync(cancellationToken);

	private MessageBuffer OwnerRequest()
	{
		using var writer = _connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
			@interface: "org.freedesktop.DBus", member: "GetNameOwner", signature: "s", flags: MessageFlags.NoAutoStart);
		writer.WriteString(Service);
		return writer.CreateMessage();
	}

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
		writer.WriteString("nori");
		writer.WriteString(content.Summary);
		writer.WriteString(content.Body);
		writer.WriteArray(content.Actions);
		writer.WriteDictionary(new Dictionary<string, VariantValue>());
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
