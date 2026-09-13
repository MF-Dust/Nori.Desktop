using System.Text.Json.Nodes;
using static Nori.Core.Tools.ToolRegistration;

namespace Nori.Core.Tools;

/// <summary>
/// 重新认识这台机器的硬件。
///
/// 能力探测本身由应用在后台做 —— 工具要不要注册取决于有没有设备，若那也交给模型去探，
/// 探测工具自己该不该注册就成了循环依赖。她平时「知道」有哪些设备，靠的是硬件清单注入
/// 提示词，不是每次去查。
///
/// 这件工具只服务一个场景：**环境变了，你让她重新看一眼**。你刚插了新键盘、刚打开 OpenRGB，
/// 跟她说「你再看看」。触发者是你，不是周期性轮询。
/// </summary>
public static class DeviceTools
{
	/// <summary>本组工具名。</summary>
	public const string RefreshName = "refreshDevices";

	/// <summary>注册本组工具，注册前先注销。</summary>
	public static void RegisterAll(ToolRegistry registry, Func<IReadOnlyList<string>>? refresh)
	{
		ArgumentNullException.ThrowIfNull(registry);

		registry.Unregister(RefreshName);
		if (refresh is null) return;

		Register(
			registry,
			RefreshName,
			"重新探测这台机器上可控的硬件设备（灯效等），返回最新的设备清单。"
				+ "只在用户说插了新设备、或刚打开了灯效软件时调用，不要主动反复调用。",
			"safe",
			Schema(),
			(_, _) =>
			{
				// 只探一次：refresh 会作废缓存并重新连接，调两次等于白做一遍。
				IReadOnlyList<string> devices = refresh();
				return Task.FromResult<object?>(new {devices, count = devices.Count});
			});
	}
}
