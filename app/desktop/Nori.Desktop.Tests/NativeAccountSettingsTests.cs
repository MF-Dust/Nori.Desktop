using System.Text.Json;
using Avalonia.Controls;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 设置窗口里的「账户与同步」页。
///
/// 这一族守的是**互斥与禁用**：登录与退出同时摆出来，其中一个必定是无效的；而未登录时
/// 备份与恢复能点，点下去只会得到一句「尚未登录」—— 两者都不会报错，只是让人白点一次。
///
/// 另外钉住一条边界：这一页显示的全部是**本机状态**。云端有没有存档要发网络请求才知道，
/// 而设置快照是同步构建且带缓存的，在那条路上发请求会让断网时整个设置窗口转圈。
/// </summary>
public partial class BridgeCommandsTests
{
	private static JsonDocument AccountSnapshot(
		bool signedIn, string email = "", int revision = 0, bool available = true, string message = "")
	{
		string json = $$"""
			{
			  "general": {"language": "zh-CN"},
			  "account": {
			    "signedIn": {{(signedIn ? "true" : "false")}},
			    "email": "{{email}}",
			    "cloudRevision": {{revision}},
			    "available": {{(available ? "true" : "false")}},
			    "lastSyncMessage": "{{message}}"
			  }
			}
			""";
		return JsonDocument.Parse(json);
	}

	private static SettingsFieldViewModel Field(AccountSettingsPage page, string key) =>
		page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == key);

	/// <summary>
	/// 登录成功要让快照失效。
	///
	/// 守的是一处实际存在过的缺陷：<c>SignInCoordinator.SignedIn</c> 没有任何订阅者，
	/// 登录成功之后只有账户窗口自己关掉，快照版本不变 —— 而这一页的每一行都从快照读。
	/// 症状是**登录完界面一切照旧**：状态行仍写着「未登录」，备份与恢复仍是禁用的，
	/// 且没有任何报错。
	///
	/// 断言快照版本而不是某个控件：读快照的界面不止这一页（还有 WebView 那一侧），
	/// 它们共用这一个通知。
	/// </summary>
	[Fact]
	public Task 登录成功之后快照要失效() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		int before = fixture._runtime.SnapshotVersion;

		fixture._services.SignIn.RaiseSignedInForTests(new Nori.Core.Cloud.CloudAccount
		{
			Email = "a@b.com",
			Token = "tok-123",
		});

		Assert.True(fixture._runtime.SnapshotVersion > before,
			"登录成功没有让快照失效 —— 设置页会一直显示未登录。");
		return Task.CompletedTask;
	});

	[Fact]
	public Task 账户页未登录时只给登录入口() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AccountSettingsPage page = new(service);

		using JsonDocument snapshot = AccountSnapshot(signedIn: false);
		page.ApplySnapshot(snapshot.RootElement);

		Assert.True(Field(page, "signIn").IsVisible);
		Assert.False(Field(page, "signOut").IsVisible);

		// 未登录时这三个动作必定失败，不该让人点得动。
		Assert.False(Field(page, "backup").Command!.CanExecute(null));
		Assert.False(Field(page, "restore").Command!.CanExecute(null));
		Assert.False(Field(page, "manage").Command!.CanExecute(null));
		// 登录本身永远可点。
		Assert.True(Field(page, "signIn").Command!.CanExecute(null));
		return Task.CompletedTask;
	});

	[Fact]
	public Task 账户页已登录时显示邮箱并放开同步() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AccountSettingsPage page = new(service);

		using JsonDocument snapshot = AccountSnapshot(signedIn: true, email: "someone@example.com", revision: 3);
		page.ApplySnapshot(snapshot.RootElement);

		Assert.False(Field(page, "signIn").IsVisible);
		Assert.True(Field(page, "signOut").IsVisible);
		Assert.True(Field(page, "backup").Command!.CanExecute(null));
		Assert.True(Field(page, "restore").Command!.CanExecute(null));

		// 登录状态那一行要写出是哪个账户 —— 一台机器上换过账户的人否则看不出来。
		Assert.Equal("someone@example.com", Field(page, "status").Text);
		Assert.Contains("3", Field(page, "revision").Text, StringComparison.Ordinal);
		return Task.CompletedTask;
	});

	/// <summary>
	/// 密钥库不可用时要说出来。
	///
	/// 否则用户看到的是「未登录」，登录一次之后还是「未登录」—— 而真正的原因是凭据存不下来。
	/// </summary>
	[Fact]
	public Task 账户页在密钥库不可用时说明原因() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AccountSettingsPage page = new(service);

		using JsonDocument snapshot = AccountSnapshot(signedIn: false, available: false);
		page.ApplySnapshot(snapshot.RootElement);

		Assert.Contains("密钥库", Field(page, "status").Text, StringComparison.Ordinal);
		return Task.CompletedTask;
	});

	/// <summary>没同步过要明说，而不是显示一个「第 0 版」。</summary>
	[Fact]
	public Task 账户页区分没同步过与已同步() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AccountSettingsPage page = new(service);

		using JsonDocument never = AccountSnapshot(signedIn: true, email: "a@b.com", revision: 0);
		page.ApplySnapshot(never.RootElement);
		Assert.DoesNotContain("第 0 版", Field(page, "revision").Text, StringComparison.Ordinal);
		Assert.Contains("还没有同步", Field(page, "revision").Text, StringComparison.Ordinal);
		return Task.CompletedTask;
	});

	/// <summary>
	/// 最近一次操作那一行从**快照**取值。
	///
	/// 页面自己存一份就要绕开只读字段的取值通路去改控件，而那会把字段标成「有未保存的
	/// 编辑」，之后的快照刷新全部被跳过 —— 那一行会永远停在第一次的结果上。
	/// </summary>
	[Fact]
	public Task 最近一次操作来自快照() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AccountSettingsPage page = new(service);

		// 什么都没做过时那一行不该占位 —— 空着的一行只是噪声。
		using JsonDocument empty = AccountSnapshot(signedIn: true, email: "a@b.com");
		page.ApplySnapshot(empty.RootElement);
		Assert.False(Field(page, "result").IsVisible);

		using JsonDocument first = AccountSnapshot(signedIn: true, email: "a@b.com", message: "已上传");
		page.ApplySnapshot(first.RootElement);
		Assert.True(Field(page, "result").IsVisible);
		Assert.Equal("已上传", Field(page, "result").Text);

		using JsonDocument second = AccountSnapshot(signedIn: true, email: "a@b.com", message: "已恢复");
		page.ApplySnapshot(second.RootElement);
		Assert.Equal("已恢复", Field(page, "result").Text);
		return Task.CompletedTask;
	});

	/// <summary>
	/// 页面注册进了设置窗口、能导航到、并且真的画得出来。
	///
	/// 只断言导航是不够的：字段种类用错、动作字段缺命令这类问题要到渲染那一刻才炸，
	/// 而那时人已经在用了。所以这里把窗口显示出来走一次布局。
	/// </summary>
	[Fact]
	public Task 账户页已注册且能渲染() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			window.UpdateLayout();

			viewModel.Navigate("account");
			Assert.Equal("account", viewModel.CurrentPage?.Key);

			SettingsPagePresenter presenter = window.FindControl<SettingsPagePresenter>("PagePresenter")!;
			presenter.RefreshPage();
			window.UpdateLayout();

			// 两个分区都在，且每个字段都有实际控件。
			Assert.Equal(2, viewModel.CurrentPage!.Sections.Count);
			Assert.NotEmpty(viewModel.CurrentPage.Sections[0].Fields);
			Assert.NotEmpty(viewModel.CurrentPage.Sections[1].Fields);
			// 动作字段必须带命令 —— 没有命令的按钮点了什么也不会发生。
			foreach (SettingsFieldViewModel field in viewModel.CurrentPage.Sections.SelectMany(s => s.Fields))
			{
				if (field.EditorKind == SettingsEditorKind.Action) Assert.NotNull(field.Command);
			}
		}
		finally
		{
			window.Close();
		}
		return Task.CompletedTask;
	});
}
