using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nori.Core.Live2D;

namespace Nori.Desktop.Models;

/// <summary>在渲染器的模型画布坐标中编辑互动矩形；不将预览容器误当模型画布。</summary>
internal sealed class ModelRegionOverlay : Control
{
	internal static readonly string[] Handles = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];
	private readonly List<Button> _handles = [];
	private IReadOnlyList<PetInteractionRegion> _regions = [];
	private Rect _viewport;
	private string? _selectedId;
	private bool _editing = true;
	private string _gesture = "";
	private Point _start;
	private PetInteractionRect? _original;
	private PetInteractionRect? _draft;
	private IPointer? _pointer;

	public ModelRegionOverlay()
	{
		Focusable = true;
		ClipToBounds = true;
		Cursor = new Cursor(StandardCursorType.Cross);
		AutomationProperties.SetName(this, "互动区域 / Interaction regions");
		foreach (string handle in Handles)
		{
			var button = new Button { Name = "ModelRegionHandle_" + handle, Tag = handle, Width = 10, Height = 10, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), IsVisible = false };
			AutomationProperties.SetName(button, "缩放 / Resize " + handle.ToUpperInvariant());
			button.AddHandler(PointerPressedEvent, (_, args) =>
			{
				if (!Editing || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
				button.Focus();
				PetInteractionRegion? selected = Regions.FirstOrDefault(region => region.Id == SelectedId);
				if (selected is not null) { _gesture = handle; _original = selected.Rect; }
				if (HasGesture) { _pointer = args.Pointer; args.Pointer.Capture(this); }
				args.Handled = true;
			}, Avalonia.Interactivity.RoutingStrategies.Tunnel);
			_handles.Add(button); LogicalChildren.Add(button); VisualChildren.Add(button);
		}
	}

	public IBrush Accent { get; set; } = Brushes.Teal;
	public IBrush Surface { get; set; } = Brushes.Black;
	public IBrush LabelBrush { get; set; } = Brushes.White;
	public string LocalReactionLabel { get; set; } = "本地 / Local";
	public event Action<string?>? SelectionChanged;
	public event Action<string, PetInteractionRect>? RectChanged;
	public event Action<PetInteractionRect>? RegionCreated;
	public event Action<string>? RegionDeleted;
	public event Action<PetInteractionRegion>? RegionTested;
	public event Action<Point>? BackgroundTested;
	public event Action? CreationFinished;

	public IReadOnlyList<PetInteractionRegion> Regions
	{
		get => _regions;
		set { _regions = value; UpdateHandles(); InvalidateVisual(); }
	}
	public Rect Viewport
	{
		get => _viewport;
		set { _viewport = value; UpdateHandles(); InvalidateVisual(); }
	}
	public string? SelectedId
	{
		get => _selectedId;
		set { _selectedId = value; UpdateHandles(); InvalidateVisual(); }
	}
	public bool Editing
	{
		get => _editing;
		set { CancelGesture(); _editing = value; Cursor = new Cursor(value ? StandardCursorType.Cross : StandardCursorType.Hand); UpdateHandles(); InvalidateVisual(); }
	}
	public bool Creating { get; set; }
	internal bool HasGesture => _gesture.Length > 0;

	private void UpdateHandles()
	{
		bool visible = Editing && SelectedId is not null && Regions.Any(region => region.Id == SelectedId) && _viewport.Width > 0 && _viewport.Height > 0;
		foreach (Button button in _handles) { button.IsVisible = visible; button.Background = LabelBrush; button.BorderBrush = Accent; }
		InvalidateMeasure(); InvalidateArrange();
	}
	protected override Size MeasureOverride(Size availableSize)
	{
		foreach (Control child in _handles) child.Measure(new Size(10, 10));
		return default;
	}
	protected override Size ArrangeOverride(Size finalSize)
	{
		PetInteractionRegion? region = Regions.FirstOrDefault(value => value.Id == SelectedId);
		if (region is not null)
			foreach (Button button in _handles)
			{
				Point center = HandlePoint(ToPixels(region.Rect), (string)button.Tag!);
				button.Arrange(new Rect(center.X - 5, center.Y - 5, 10, 10));
			}
		return finalSize;
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
		if (_viewport.Width <= 0 || _viewport.Height <= 0) return;
		if (Editing) context.DrawRectangle(null, new Pen(Accent, 1, DashStyle.Dash), _viewport);
		foreach (PetInteractionRegion region in Regions)
		{
			Rect rect = ToPixels(region.Rect);
			bool selected = region.Id == SelectedId;
			using (context.PushOpacity(selected ? 0.22 : 0.1)) context.DrawRectangle(Accent, null, rect);
			context.DrawRectangle(null, new Pen(Accent, selected ? 2 : 1), rect);
			string label = region.Name + " · " + (region.ReactionMode == PetInteractionReactionMode.Ai ? "AI" : LocalReactionLabel);
			var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI, sans-serif"), 12, LabelBrush);
			double labelX = Math.Clamp(rect.X, 0, Math.Max(0, Bounds.Width - text.Width - 8));
			double labelY = Math.Clamp(rect.Y - 23, 0, Math.Max(0, Bounds.Height - 23));
			context.DrawRectangle(Surface, null, new Rect(labelX, labelY, Math.Min(text.Width + 8, Bounds.Width), 22), 3, 3);
			context.DrawText(text, new Point(labelX + 4, labelY + 2));
			if (!selected || !Editing) continue;
			foreach (string handle in Handles)
			{
				Point center = HandlePoint(rect, handle);
				context.DrawRectangle(LabelBrush, new Pen(Accent, 1), new Rect(center.X - 5, center.Y - 5, 10, 10), 2, 2);
			}
		}
		if (_draft is { } draft) context.DrawRectangle(null, new Pen(Accent, 2, DashStyle.Dash), ToPixels(draft));
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
		Focus();
		BeginGesture(e.GetPosition(this));
		if (HasGesture) { _pointer = e.Pointer; e.Pointer.Capture(this); }
		e.Handled = true;
	}
	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (HasGesture) { UpdateGesture(e.GetPosition(this)); e.Handled = true; }
	}
	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!HasGesture) return;
		UpdateGesture(e.GetPosition(this)); EndGesture(); e.Pointer.Capture(null); _pointer = null; e.Handled = true;
	}
	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		CancelGesture();
	}
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		string? handle = _handles.FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
		if (HandleKey(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift), handle)) e.Handled = true;
	}

	internal void BeginGesture(Point pixel)
	{
		if (!TryNormalize(pixel, out Point point)) return;
		PetInteractionRegion? hit = Hit(point);
		if (!Editing)
		{
			if (hit is not null) RegionTested?.Invoke(hit); else BackgroundTested?.Invoke(pixel);
			return;
		}
		_start = point;
		PetInteractionRegion? selected = Regions.FirstOrDefault(region => region.Id == SelectedId);
		if (!Creating && selected is not null)
		{
			string? handle = Handles.FirstOrDefault(value =>
			{
				Point center = HandlePoint(ToPixels(selected.Rect), value);
				return Math.Abs(center.X - pixel.X) <= 8 && Math.Abs(center.Y - pixel.Y) <= 8;
			});
			if (handle is not null) { _gesture = handle; _original = selected.Rect; return; }
		}
		if (!Creating && hit is not null)
		{
			Select(hit.Id); _gesture = "move"; _original = hit.Rect;
		}
		else
		{
			Select(null); _gesture = "create"; _draft = FromPoints(point, point);
		}
	}
	internal void UpdateGesture(Point pixel)
	{
		if (!TryNormalize(pixel, out Point point) || !HasGesture) return;
		if (_gesture == "create") { _draft = FromPoints(_start, point); InvalidateVisual(); return; }
		if (_original is null || SelectedId is null) return;
		PetInteractionRect rect = _gesture == "move"
			? Move(_original, point.X - _start.X, point.Y - _start.Y)
			: Resize(_original, _gesture, point);
		RectChanged?.Invoke(SelectedId, rect);
	}
	internal void EndGesture()
	{
		PetInteractionRect? draft = _draft;
		bool create = _gesture == "create" && draft is { Width: >= 0.02, Height: >= 0.02 };
		_gesture = ""; _draft = null; _original = null;
		IPointer? pointer = _pointer; _pointer = null; pointer?.Capture(null);
		if (create) { RegionCreated?.Invoke(draft!); Creating = false; CreationFinished?.Invoke(); }
		InvalidateVisual();
	}
	internal void CancelGesture()
	{
		if (_original is not null && SelectedId is not null) RectChanged?.Invoke(SelectedId, _original);
		_gesture = ""; _draft = null; _original = null;
		IPointer? pointer = _pointer; _pointer = null; pointer?.Capture(null);
		InvalidateVisual();
	}
	internal bool HandleKey(Key key, bool shift = false, string? handle = null)
	{
		if (!Editing) return false;
		if (key == Key.Escape && (HasGesture || Creating || SelectedId is not null))
		{
			CancelGesture(); Creating = false; CreationFinished?.Invoke(); Select(null); return true;
		}
		if (SelectedId is not { } id) return false;
		if (key is Key.Delete or Key.Back) { RegionDeleted?.Invoke(id); return true; }
		PetInteractionRegion? region = Regions.FirstOrDefault(value => value.Id == id);
		if (region is null) return false;
		double step = shift ? 0.05 : 0.01;
		Vector delta = key switch { Key.Left => new(-step, 0), Key.Right => new(step, 0), Key.Up => new(0, -step), Key.Down => new(0, step), _ => default };
		if (delta == default) return false;
		PetInteractionRect rect = handle is null ? Move(region.Rect, delta.X, delta.Y)
			: Resize(region.Rect, handle, HandlePoint(new Rect(region.Rect.X, region.Rect.Y, region.Rect.Width, region.Rect.Height), handle) + delta);
		RectChanged?.Invoke(id, rect); return true;
	}

	private void Select(string? id) { SelectedId = id; SelectionChanged?.Invoke(id); }
	private bool TryNormalize(Point pixel, out Point point)
	{
		point = default;
		if (_viewport.Width <= 0 || _viewport.Height <= 0) return false;
		point = new Point((pixel.X - _viewport.X) / _viewport.Width, (pixel.Y - _viewport.Y) / _viewport.Height);
		return double.IsFinite(point.X) && double.IsFinite(point.Y);
	}
	private PetInteractionRegion? Hit(Point point) => Regions.Where(region => region.Rect.Contains(point.X, point.Y)).OrderBy(region => region.Rect.Area).FirstOrDefault();
	private Rect ToPixels(PetInteractionRect rect) => new(_viewport.X + rect.X * _viewport.Width, _viewport.Y + rect.Y * _viewport.Height, rect.Width * _viewport.Width, rect.Height * _viewport.Height);
	internal static Point HandlePoint(Rect rect, string handle) => new(handle.Contains('w') ? rect.Left : handle.Contains('e') ? rect.Right : rect.Center.X, handle.Contains('n') ? rect.Top : handle.Contains('s') ? rect.Bottom : rect.Center.Y);
	internal static PetInteractionRect FromPoints(Point first, Point second)
	{
		double x1 = Math.Clamp(first.X, 0, 1), y1 = Math.Clamp(first.Y, 0, 1), x2 = Math.Clamp(second.X, 0, 1), y2 = Math.Clamp(second.Y, 0, 1);
		return new() { X = Math.Min(x1, x2), Y = Math.Min(y1, y2), Width = Math.Abs(x2 - x1), Height = Math.Abs(y2 - y1) };
	}
	internal static PetInteractionRect Move(PetInteractionRect rect, double dx, double dy) => rect with { X = Math.Clamp(rect.X + dx, 0, 1 - rect.Width), Y = Math.Clamp(rect.Y + dy, 0, 1 - rect.Height) };
	internal static PetInteractionRect Resize(PetInteractionRect rect, string handle, Point point)
	{
		double x1 = rect.X, y1 = rect.Y, x2 = rect.X + rect.Width, y2 = rect.Y + rect.Height;
		// 旧配置允许 0.01 的边缘区域；固定边不能容纳 0.02 时保留可用空间。
		if (handle.Contains('w')) x1 = Math.Clamp(point.X, 0, Math.Max(0, x2 - 0.02));
		if (handle.Contains('e')) x2 = Math.Clamp(point.X, Math.Min(1, x1 + 0.02), 1);
		if (handle.Contains('n')) y1 = Math.Clamp(point.Y, 0, Math.Max(0, y2 - 0.02));
		if (handle.Contains('s')) y2 = Math.Clamp(point.Y, Math.Min(1, y1 + 0.02), 1);
		return new() { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1 };
	}
}
