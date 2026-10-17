using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Nori.Core.Community;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>设置窗口「关于」页的贡献者头像墙。</summary>
public partial class BridgeCommandsTests
{
	/// <summary>8×8 纯色 PNG (主色 #77c6d4)。</summary>
	private static readonly byte[] TinyPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mMoP3YFK2IYWhIAlYWEQTTJs/wAAAAASUVORK5CYII=");

	[Fact]
	public Task 关于页从贡献者缓存显示头像且跳过损坏头像() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		File.WriteAllText(fixture._services.Paths.ContributorsCachePath, GitHubContributorCatalog.ToCacheJson(
			new GitHubContributorCatalog.ContributorSnapshot(DateTimeOffset.UtcNow,
				[new GitHubContributor("alice", TinyPng), new GitHubContributor("broken", [1, 2, 3])])));
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AboutSettingsPage page = new(service);
		try
		{
			using JsonDocument snapshot = JsonDocument.Parse("""{"app":{"safeMode":true}}""");
			page.ApplySnapshot(snapshot.RootElement);
			SettingsFieldViewModel contributors = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "contributors");
			Assert.Equal(SettingsEditorKind.Avatars, contributors.EditorKind);

			await WaitUntilAsync(() => contributors.Avatars.Count > 0);
			Assert.Equal(["alice"], contributors.Avatars.Select(avatar => avatar.Name));
			Assert.Equal(string.Empty, contributors.Text);
		}
		finally
		{
			page.Dispose();
		}
	});

	[Fact]
	public Task 关于页安全模式无缓存时提示而不报错() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		File.Delete(fixture._services.Paths.ContributorsCachePath);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		AboutSettingsPage page = new(service);
		try
		{
			using JsonDocument snapshot = JsonDocument.Parse("""{"app":{"safeMode":true}}""");
			page.ApplySnapshot(snapshot.RootElement);
			SettingsFieldViewModel contributors = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "contributors");

			await WaitUntilAsync(() => contributors.Text.Contains("安全模式", StringComparison.Ordinal));
			Assert.Empty(contributors.Avatars);
		}
		finally
		{
			page.Dispose();
		}
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task 关于页头像墙在设置窗口内换行显示(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		GitHubContributor[] people = Enumerable.Range(1, 24).Select(index => new GitHubContributor($"user{index}", TinyPng)).ToArray();
		File.WriteAllText(fixture._services.Paths.ContributorsCachePath, GitHubContributorCatalog.ToCacheJson(
			new GitHubContributorCatalog.ContributorSnapshot(DateTimeOffset.UtcNow, people)));
		SettingsWindow window = new(fixture._services) {Width = width, Height = height};
		try
		{
			window.Show();
			SettingsViewModel vm = Assert.IsType<SettingsViewModel>(window.DataContext);
			await vm.RefreshSnapshotAsync();
			vm.Navigate("about");
			SettingsPageBase about = vm.Groups.SelectMany(group => group.Pages).Single(page => page.Key == "about").Page;
			SettingsFieldViewModel contributors = about.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "contributors");
			await WaitUntilAsync(() => contributors.Avatars.Count == people.Length);
			window.UpdateLayout();

			WrapPanel wall = window.GetVisualDescendants().OfType<WrapPanel>().Single(panel => panel.Children.Count == people.Length);
			Assert.True(wall.IsEffectivelyVisible);
			Assert.True(wall.Bounds.Width > 0 && wall.Bounds.Height > 0);
			Point? origin = wall.TranslatePoint(default, window);
			Assert.NotNull(origin);
			Assert.InRange(origin.Value.X + wall.Bounds.Width, 0, window.ClientSize.Width);
			CaptureUi(window, $"settings-about-contributors-{width}x{height}");
		}
		finally { window.AllowClose = true; window.Close(); }
	});
}
