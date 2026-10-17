using System.Text.Json;
using Avalonia.Media.Imaging;
using Nori.Core;

namespace Nori.Desktop.Settings;

/// <summary>关于 Nori 与运行环境信息页。</summary>
public sealed class AboutSettingsPage : SettingsPageBase
{
	/// <summary>头像解码宽度：界面按 40px 显示，按 2 倍缩放解码。</summary>
	private const int AvatarDecodeWidth = 80;

	private readonly SettingsFieldViewModel _contributors;
	private SettingsText? _contributorStatus = new("正在从 GitHub 获取贡献者…", "Loading contributors from GitHub…");
	private IReadOnlyList<SettingsAvatar> _avatars = [];
	private bool _contributorsRequested;
	private bool _disposed;

	/// <summary>创建关于页面。</summary>
	public AboutSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "about", "app", new("关于 Nori", "About Nori"), new("版本、许可证和运行环境信息。", "Version, license and runtime information."), lifetimeToken)
	{
		SettingsSectionViewModel identity = AddSection(new("Nori Desktop Pet", "Nori Desktop Pet"));
		AddField(identity, "version", new("版本", "Version"), new("当前应用版本。", "The current application version."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, ProductVersion.Current, ["app", "productVersion"], ["app", "appVersion"]), ProductVersion.Current,
			(_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(identity, "license", new("许可证", "License"), new("项目源代码许可证。", "License for the project source code."), SettingsEditorKind.Text,
			_ => "GPL-3.0", "GPL-3.0", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		_contributors = AddField(identity, "contributors", new("贡献者", "Contributors"),
			new("社区维护的非官方开源项目，与官方无关。头像来自项目的 GitHub 贡献者。", "An unofficial community-maintained open-source project, unaffiliated with the official project. Avatars come from the project's GitHub contributors."),
			SettingsEditorKind.Avatars, _ => ContributorStatusText(), string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

		SettingsSectionViewModel environment = AddSection(new("运行环境", "Runtime environment"));
		AddField(environment, "renderer", new("渲染引擎", "Renderer"), new("当前界面由 Avalonia 原生控件绘制。", "The interface is drawn with native Avalonia controls."), SettingsEditorKind.Text,
			snapshot => Renderer(snapshot), RendererFallback(), (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(environment, "safeMode", new("安全模式", "Safe mode"), new("安全模式会关闭外部网络和后台自动任务。", "Safe mode disables external network and background tasks."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "app", "safeMode") ? "已启用 / Enabled" : "未启用 / Disabled",
			"未启用 / Disabled", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
	}

	/// <inheritdoc />
	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		if (_contributorsRequested) return;
		// 首个快照到达时才查询：此时页面已挂到设置窗口，并且知道是否处于安全模式。
		_contributorsRequested = true;
		bool safeMode = SettingsSnapshotReader.Boolean(snapshot, false, "app", "safeMode");
		_ = LoadContributorsAsync(safeMode);
	}

	private async Task LoadContributorsAsync(bool safeMode)
	{
		try
		{
			JsonElement result = await ExecuteAsync("about_list_contributors", null, LifetimeToken).ConfigureAwait(true);
			IReadOnlyList<SettingsAvatar> avatars = await Task.Run(() => DecodeAvatars(result), LifetimeToken).ConfigureAwait(true);
			if (_disposed)
			{
				foreach (SettingsAvatar avatar in avatars) avatar.Image.Dispose();
				return;
			}
			_avatars = avatars;
			_contributors.Avatars = avatars;
			SetContributorStatus(avatars.Count > 0
				? null
				: safeMode
					? new("安全模式下不联网，暂无本地缓存的贡献者。", "Safe mode is offline and no contributors are cached yet.")
					: new("暂无可显示的贡献者。", "No contributors to show yet."));
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) when (_disposed) { }
		catch (Exception exception)
		{
			if (_disposed) return;
			// 贡献者只是展示信息，失败时在原处给出原因，下次打开设置窗口再重试。
			string message = exception.Message.Trim();
			SetContributorStatus(new(message.Length > 0 ? message : "无法获取 GitHub 贡献者，请稍后再试。", "Could not load contributors from GitHub. Try again later."));
		}
	}

	/// <summary>把桥接结果解码为头像位图；单个头像损坏时跳过，不影响其他头像。</summary>
	internal static IReadOnlyList<SettingsAvatar> DecodeAvatars(JsonElement result)
	{
		if (result.ValueKind != JsonValueKind.Object
			|| !result.TryGetProperty("contributors", out JsonElement contributors)
			|| contributors.ValueKind != JsonValueKind.Array) return [];
		List<SettingsAvatar> avatars = [];
		foreach (JsonElement item in contributors.EnumerateArray())
		{
			string login = SettingsSnapshotReader.String(item, string.Empty, "login");
			string data = SettingsSnapshotReader.String(item, string.Empty, "avatar");
			if (login.Length == 0 || data.Length == 0) continue;
			try
			{
				using MemoryStream stream = new(Convert.FromBase64String(data), writable: false);
				avatars.Add(new SettingsAvatar(login, Bitmap.DecodeToWidth(stream, AvatarDecodeWidth, BitmapInterpolationMode.HighQuality)));
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// 头像数据无法解码时只少显示这一位。
			}
		}
		return avatars;
	}

	private void SetContributorStatus(SettingsText? status)
	{
		_contributorStatus = status;
		_contributors.Text = ContributorStatusText();
	}

	private string ContributorStatusText() =>
		_contributorStatus is { } status ? (IsEnglish ? status.English : status.Chinese) : string.Empty;

	/// <inheritdoc />
	protected override void OnDisposed()
	{
		_disposed = true;
		_contributors.Avatars = [];
		foreach (SettingsAvatar avatar in _avatars) avatar.Image.Dispose();
		_avatars = [];
	}

	private static string Renderer(JsonElement snapshot)
	{
		string os = SettingsSnapshotReader.String(snapshot, string.Empty, "platform", "os");
		return os switch
		{
			"windows" or "macos" or "linux" => "Avalonia UI",
			_ => RendererFallback(),
		};
	}

	private static string RendererFallback() => "Avalonia UI";

	private static string FirstString(JsonElement root, string fallback, params string[][] paths)
	{
		foreach (string[] path in paths)
		{
			string value = SettingsSnapshotReader.String(root, string.Empty, path);
			if (value.Length > 0) return value;
		}
		return fallback;
	}
}
