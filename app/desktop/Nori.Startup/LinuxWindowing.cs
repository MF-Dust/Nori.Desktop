using Avalonia;
using Tmds.DBus.Protocol;

namespace Nori.Startup;

/// <summary>有 XWayland 时保留桌宠定位与穿透能力；无 DISPLAY 时直接使用原生 Wayland。</summary>
internal static class LinuxWindowing
{
	/// <summary>与 Avalonia.FreeDesktop 的 FcitxX11TextInputMethod 监听的总线名一致。</summary>
	private static readonly string[] FcitxBusNames = ["org.freedesktop.portal.Fcitx", "org.fcitx.Fcitx"];

	/// <summary>与 Avalonia.FreeDesktop 的 IBusX11TextInputMethod 监听的总线名一致。</summary>
	private static readonly string[] IBusBusNames = ["org.freedesktop.portal.IBus"];

	private static readonly TimeSpan BusProbeTimeout = TimeSpan.FromSeconds(2);

	internal static AppBuilder UseNoriLinuxBackend(this AppBuilder builder)
	{
		if (!OperatingSystem.IsLinux()) return builder;
		if (UsesWayland(Environment.GetEnvironmentVariable("DISPLAY"),
			Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")))
			return builder.With(CreateWaylandOptions()).UseWayland();
		// X11 / XWayland 的输入法发现只看环境变量；KDE Wayland 默认不导出它们。
		ApplyX11InputMethod();
		return builder;
	}

	/// <summary>宿主自带窗口外观，禁止重建 Wayland 表面后重新协商出系统标题栏。</summary>
	internal static WaylandPlatformOptions CreateWaylandOptions()
	{
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
		return new WaylandPlatformOptions { ForceDrawnDecorations = true };
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
	}

	internal static bool UsesWayland(string? display, string? waylandDisplay, string? sessionType) =>
		string.IsNullOrWhiteSpace(display) && (!string.IsNullOrWhiteSpace(waylandDisplay)
			|| string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Avalonia 12 仅在 <c>AVALONIA_IM_MODULE</c> / <c>GTK_IM_MODULE</c> / <c>QT_IM_MODULE</c>
	/// 为 fcitx、fcitx5、ibus，或 <c>XMODIFIERS</c> 含 <c>@im=</c> 时连接输入法。
	/// <c>none</c> 表示用户明确关闭。其余值（如 wayland、xim）不会让 Avalonia 连上 Fcitx。
	/// </summary>
	internal static bool NeedsX11InputMethodProbe(string? avaloniaIm, string? gtkIm, string? qtIm, string? xmodifiers)
	{
		if (IsDisabled(avaloniaIm) || IsDisabled(gtkIm) || IsDisabled(qtIm)) return false;
		if (IsDbusModule(avaloniaIm) || IsDbusModule(gtkIm) || IsDbusModule(qtIm)) return false;
		return xmodifiers?.Contains("@im=", StringComparison.Ordinal) != true;
	}

	/// <summary>
	/// 返回应写入本进程 <c>XMODIFIERS</c> 的值。已有 Avalonia 能识别的选择、或会话里没有对应服务时返回 null。
	/// 只设置 <c>XMODIFIERS</c>，不改 <c>GTK_IM_MODULE</c> / <c>QT_IM_MODULE</c>。
	/// </summary>
	internal static string? SelectX11InputMethodModifiers(string? avaloniaIm, string? gtkIm, string? qtIm, string? xmodifiers, bool fcitxReady, bool ibusReady)
	{
		if (!NeedsX11InputMethodProbe(avaloniaIm, gtkIm, qtIm, xmodifiers)) return null;
		if (fcitxReady) return "@im=fcitx";
		if (ibusReady) return "@im=ibus";
		return null;
	}

	/// <summary>
	/// 在 Avalonia 初始化 X11 之前补上输入法变量。探测失败保持原环境，不阻断启动。
	/// </summary>
	internal static void ApplyX11InputMethod()
	{
		string? avaloniaIm = Environment.GetEnvironmentVariable("AVALONIA_IM_MODULE");
		string? gtkIm = Environment.GetEnvironmentVariable("GTK_IM_MODULE");
		string? qtIm = Environment.GetEnvironmentVariable("QT_IM_MODULE");
		string? xmodifiers = Environment.GetEnvironmentVariable("XMODIFIERS");
		if (!NeedsX11InputMethodProbe(avaloniaIm, gtkIm, qtIm, xmodifiers)) return;
		bool fcitxReady = AnyBusNameOwned(DBusAddress.Session, FcitxBusNames);
		bool ibusReady = !fcitxReady && AnyBusNameOwned(DBusAddress.Session, IBusBusNames);
		string? selected = SelectX11InputMethodModifiers(avaloniaIm, gtkIm, qtIm, xmodifiers, fcitxReady, ibusReady);
		if (selected is not null) Environment.SetEnvironmentVariable("XMODIFIERS", selected);
	}

	/// <summary>当前会话总线是否已有 Avalonia 能连上的 Fcitx 门户或经典接口。</summary>
	internal static bool SessionOwnsFcitx() => AnyBusNameOwned(DBusAddress.Session, FcitxBusNames);

	private static bool IsDisabled(string? module) => string.Equals(module, "none", StringComparison.Ordinal);

	private static bool IsDbusModule(string? module) => module is "fcitx" or "fcitx5" or "ibus";

	/// <summary>会话总线不可用或超时时视为没有该输入法，避免启动被卡住。</summary>
	private static bool AnyBusNameOwned(string? address, string[] names)
	{
		if (string.IsNullOrWhiteSpace(address) || names.Length == 0) return false;
		try
		{
			// 启动早期还没有 Avalonia 同步上下文；放到线程池上等待，避免 GetResult 卡死。
			return Task.Run(() => AnyBusNameOwnedAsync(address, names)).GetAwaiter().GetResult();
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static async Task<bool> AnyBusNameOwnedAsync(string address, string[] names)
	{
		using CancellationTokenSource cancel = new(BusProbeTimeout);
		using DBusConnection connection = new(address);
		await connection.ConnectAsync().AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
		foreach (string name in names)
		{
			if (await NameIsOwnedAsync(connection, name, cancel.Token).ConfigureAwait(false)) return true;
		}
		return false;
	}

	private static async Task<bool> NameIsOwnedAsync(DBusConnection connection, string name, CancellationToken cancellationToken)
	{
		Task<bool> call = connection.CallMethodAsync(CreateNameHasOwner(connection, name), static (message, _) =>
		{
			Reader reader = message.GetBodyReader();
			reader.ReadSignature("b");
			return reader.ReadBool();
		});
		try
		{
			return await call.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			Observe(call);
			throw;
		}
	}

	private static MessageBuffer CreateNameHasOwner(DBusConnection connection, string name)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(
			destination: "org.freedesktop.DBus",
			path: "/org/freedesktop/DBus",
			@interface: "org.freedesktop.DBus",
			member: "NameHasOwner",
			signature: "s",
			flags: MessageFlags.NoAutoStart);
		writer.WriteString(name);
		return writer.CreateMessage();
	}

	private static void Observe(Task task) => task.ContinueWith(
		static completed => _ = completed.Exception,
		CancellationToken.None,
		TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
		TaskScheduler.Default);
}
