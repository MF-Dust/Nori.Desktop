using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Nori.Core.Live2D;
using Nori.Desktop.Models;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeModelRegionsMapViewportCreateReverseClampAndRejectTinyDrags() => WithSettingsUiAsync(() =>
	{
		var overlay = new ModelRegionOverlay { Viewport = new Rect(50, 100, 200, 300) };
		List<PetInteractionRect> created = [];
		overlay.RegionCreated += created.Add;
		overlay.BeginGesture(new Point(190, 370)); overlay.UpdateGesture(new Point(110, 220)); overlay.EndGesture();
		PetInteractionRect rect = Assert.Single(created);
		Assert.Equal(0.3, rect.X, 6); Assert.Equal(0.4, rect.Y, 6); Assert.Equal(0.4, rect.Width, 6); Assert.Equal(0.5, rect.Height, 6);
		overlay.BeginGesture(new Point(-50, -50)); overlay.UpdateGesture(new Point(500, 500)); overlay.EndGesture();
		Assert.Equal(new PetInteractionRect { X = 0, Y = 0, Width = 1, Height = 1 }, created[1]);
		overlay.BeginGesture(new Point(100, 150)); overlay.UpdateGesture(new Point(101, 151)); overlay.EndGesture();
		Assert.Equal(2, created.Count);
		overlay.Viewport = default; overlay.BeginGesture(new Point(100, 100)); Assert.False(overlay.HasGesture);
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("nw")][InlineData("n")][InlineData("ne")][InlineData("e")]
	[InlineData("se")][InlineData("s")][InlineData("sw")][InlineData("w")]
	public Task NativeModelRegionsSupportEveryPointerAndKeyboardResizeHandle(string handle) => WithSettingsUiAsync(() =>
	{
		var initial = new PetInteractionRect { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 };
		var overlay = new ModelRegionOverlay { Viewport = new Rect(0, 0, 400, 400), Regions = [Region("head", initial)], SelectedId = "head" };
		PetInteractionRect? changed = null;
		overlay.RectChanged += (_, rect) => { changed = rect; overlay.Regions = [Region("head", rect)]; };
		Point start = ModelRegionOverlay.HandlePoint(new Rect(100, 100, 200, 200), handle);
		overlay.BeginGesture(start); overlay.UpdateGesture(start + new Vector(handle.Contains('w') ? -20 : 20, handle.Contains('n') ? -20 : 20)); overlay.EndGesture();
		Assert.NotNull(changed); Assert.NotEqual(initial, changed);
		Assert.InRange(changed.X, 0, 1 - changed.Width); Assert.InRange(changed.Y, 0, 1 - changed.Height);
		PetInteractionRect pointerRect = changed;
		Assert.True(overlay.HandleKey(handle.Contains('w') || handle.Contains('e') ? Key.Right : Key.Down, false, handle));
		Assert.NotEqual(pointerRect, changed);
		// 后端旧配置的最小边缘区域比新建手柄的 0.02 下限更小，不能抛出 Clamp 异常。
		foreach (double edge in new[] { 0, 0.99 })
		{
			PetInteractionRect tiny = new() { X = edge, Y = edge, Width = 0.01, Height = 0.01 };
			PetInteractionRect resized = ModelRegionOverlay.Resize(tiny, handle, new Point(edge, edge));
			Assert.True(resized.Width > 0 && resized.Height > 0); Assert.InRange(resized.X + resized.Width, 0, 1); Assert.InRange(resized.Y + resized.Height, 0, 1);
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task NativeModelRegionsKeepMovementSizeCancelAndSmallestStableHitPriority() => WithSettingsUiAsync(() =>
	{
		var body = Region("body", new PetInteractionRect { X = 0.1, Y = 0.2, Width = 0.8, Height = 0.7 });
		var badge = Region("badge", new PetInteractionRect { X = 0.4, Y = 0.4, Width = 0.2, Height = 0.2 });
		var overlay = new ModelRegionOverlay { Viewport = new Rect(-100, -125, 600, 750), Regions = [body, badge, badge with { Id = "equal" }], Editing = false };
		PetInteractionRegion? tested = null; int background = 0;
		overlay.RegionTested += region => tested = region; overlay.BackgroundTested += _ => background++;
		overlay.BeginGesture(new Point(200, 250)); Assert.Equal("badge", tested?.Id); Assert.False(overlay.HasGesture);
		overlay.BeginGesture(new Point(-99, -124)); Assert.Equal(1, background);
		overlay.Editing = true; overlay.SelectedId = "badge";
		overlay.RectChanged += (_, rect) => overlay.Regions = [body, badge with { Rect = rect }];
		overlay.BeginGesture(new Point(200, 250)); overlay.UpdateGesture(new Point(900, 900));
		PetInteractionRect moved = overlay.Regions[1].Rect; Assert.Equal(0.2, moved.Width); Assert.Equal(0.2, moved.Height); Assert.Equal(0.8, moved.X);
		overlay.CancelGesture(); Assert.Equal(badge.Rect, overlay.Regions[1].Rect);
		Assert.True(overlay.HandleKey(Key.Left)); Assert.Equal(0.39, overlay.Regions[1].Rect.X, 6);
		Assert.True(overlay.HandleKey(Key.Up, true)); Assert.Equal(0.35, overlay.Regions[1].Rect.Y, 6);
		string? removed = null; overlay.RegionDeleted += id => removed = id;
		Assert.True(overlay.HandleKey(Key.Delete)); Assert.Equal("badge", removed);
		Assert.True(overlay.HandleKey(Key.Escape)); Assert.Null(overlay.SelectedId);
		Assert.False(overlay.HandleKey(Key.Delete));
		return Task.CompletedTask;
	});

	[Fact]
	public Task NativeModelRegionShortcutsRespectTextFocusAndAllHandlesAreFocusable() => WithSettingsUiAsync(() =>
	{
		var region = Region("head", new PetInteractionRect { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 });
		var overlay = new ModelRegionOverlay { Viewport = new Rect(0, 0, 400, 300), Regions = [region], SelectedId = region.Id };
		var text = new TextBox { Text = "区域名称", Name = "RegionText" };
		var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; root.Children.Add(text); Grid.SetRow(overlay, 1); root.Children.Add(overlay);
		var window = new Window { Width = 400, Height = 360, Content = root };
		int changed = 0; overlay.RectChanged += (_, rect) => { changed++; region = region with { Rect = rect }; overlay.Regions = [region]; };
		try
		{
			window.Show(); window.UpdateLayout(); text.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Assert.Equal(0, changed);
			Button[] handles = overlay.GetVisualDescendants().OfType<Button>().ToArray(); Assert.Equal(8, handles.Length);
			foreach (Button handle in handles)
			{
				Assert.True(handle.Focusable); Assert.True(handle.IsVisible); Assert.True(handle.Focus()); Assert.True(handle.IsKeyboardFocusWithin);
				window.KeyPressQwerty((string)handle.Tag! is "n" or "s" ? PhysicalKey.ArrowDown : PhysicalKey.ArrowRight, RawInputModifiers.None);
			}
			Assert.Equal(8, changed);
			overlay.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); Assert.Equal(9, changed);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public void NativeModelRegionBindingsRetainLegacyValidationAndLocalCoordinates()
	{
		PetInteractionRegion region = Region("head", new PetInteractionRect { X = 0.2, Y = 0.4, Width = 0.4, Height = 0.4 }) with
		{
			Motion = new PetInteractionAction { Mode = PetInteractionActionMode.Selected, Group = "tap_body", Name = "tap_01" },
			Expression = new PetInteractionAction { Mode = PetInteractionActionMode.Selected, Name = "Happy" },
		};
		PetInteractionConfig config = new() { Regions = [region] };
		MotionGroupInfo[] motions = [new() { Group = "tap_body", Names = ["tap_01", "tap_02"] }];
		config.ValidateBindings(motions, ["Happy"]);
		Assert.Throws<InvalidOperationException>(() => config.ValidateBindings([], ["Happy"]));
		Assert.Throws<InvalidOperationException>(() => config.ValidateBindings(motions, []));
		config = new() { Regions = [region with { Motion = PetInteractionAction.Random, Expression = PetInteractionAction.None }] };
		config.ValidateBindings([], []);
		Assert.True(PetInteractionResolver.TryResolve(config, 0.4, 0.6, out PetInteractionHit? hit));
		Assert.NotNull(hit); Assert.Equal(0.5, hit.RegionX, 6); Assert.Equal(0.5, hit.RegionY, 6);
		Assert.False(PetInteractionResolver.TryResolve(config, -0.1, 0.5, out _));
	}
	private static PetInteractionRegion Region(string id, PetInteractionRect rect) => new() { Id = id, Name = id, Rect = rect };
}
