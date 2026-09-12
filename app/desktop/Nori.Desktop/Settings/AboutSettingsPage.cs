using Nori.Core;

namespace Nori.Desktop.Settings;

/// <summary>关于 Nori 与运行环境信息页。</summary>
public sealed class AboutSettingsPage : SettingsPageBase
{
	/// <summary>创建关于页面。</summary>
	public AboutSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "about", "system", new("关于 Nori", "About Nori"), new("版本、许可证和运行环境信息。", "Version, license and runtime information."), lifetimeToken)
	{
		SettingsSectionViewModel identity = AddSection(new("Nori Desktop Pet", "Nori Desktop Pet"));
		AddField(identity, "version", new("版本", "Version"), new("当前应用版本。", "The current application version."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, ProductVersion.Current, ["app", "productVersion"], ["app", "appVersion"]), ProductVersion.Current,
			(_, _) => Task.FromResult(default(System.Text.Json.JsonElement)), readOnly: true);
		AddField(identity, "license", new("许可证", "License"), new("项目源代码许可证。", "License for the project source code."), SettingsEditorKind.Text,
			_ => "GPL-3.0", "GPL-3.0", (_, _) => Task.FromResult(default(System.Text.Json.JsonElement)), readOnly: true);
		AddField(identity, "authors", new("作者", "Authors"), new("社区维护的非官方开源项目，与官方无关。", "An unofficial community-maintained open-source project, unaffiliated with the official project."), SettingsEditorKind.Text,
			_ => "erhio · Nori · qicajie", "Nori Desktop Pet contributors", (_, _) => Task.FromResult(default(System.Text.Json.JsonElement)), readOnly: true);

		SettingsSectionViewModel environment = AddSection(new("运行环境", "Runtime environment"));
		AddField(environment, "renderer", new("渲染引擎", "Renderer"), new("当前平台使用的原生 WebView。", "The native WebView used on this platform."), SettingsEditorKind.Text,
			snapshot => Renderer(snapshot), RendererFallback(), (_, _) => Task.FromResult(default(System.Text.Json.JsonElement)), readOnly: true);
		AddField(environment, "safeMode", new("安全模式", "Safe mode"), new("安全模式会关闭外部网络和后台自动任务。", "Safe mode disables external network and background tasks."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "app", "safeMode") ? "已启用 / Enabled" : "未启用 / Disabled",
			"未启用 / Disabled", (_, _) => Task.FromResult(default(System.Text.Json.JsonElement)), readOnly: true);
	}

	private static string Renderer(System.Text.Json.JsonElement snapshot)
	{
		string os = SettingsSnapshotReader.String(snapshot, string.Empty, "platform", "os");
		return os switch
		{
			"windows" => "Avalonia UI + Microsoft WebView2",
			"macos" => "Avalonia UI + WKWebView",
			"linux" => "Avalonia UI + WebKitGTK",
			_ => RendererFallback(),
		};
	}

	private static string RendererFallback() => OperatingSystem.IsWindows() ? "WebView2" : OperatingSystem.IsMacOS() ? "WKWebView" : "WebKitGTK";

	private static string FirstString(System.Text.Json.JsonElement root, string fallback, params string[][] paths)
	{
		foreach (string[] path in paths)
		{
			string value = SettingsSnapshotReader.String(root, string.Empty, path);
			if (value.Length > 0) return value;
		}
		return fallback;
	}
}
