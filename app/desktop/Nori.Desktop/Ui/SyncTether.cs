using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Nori.Desktop.Account;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Ui;

/// <summary>两端各自的状态，决定端点绘制为实心还是空心。</summary>
internal enum TetherState
{
	/// <summary>未登录。两端状态均未知，整体降低不透明度。</summary>
	SignedOut,

	/// <summary>已登录，云端无存档。仅本机端点为实心。</summary>
	NoRemote,

	/// <summary>两端版本一致。连线为实线。</summary>
	InStep,

	/// <summary>两端均有存档但版本不一致。连线为虚线。</summary>
	OutOfStep,
}

/// <summary>数据传输方向。</summary>
internal enum TetherDirection
{
	/// <summary>本机 → 云端（备份）。</summary>
	Up,

	/// <summary>云端 → 本机（恢复）。</summary>
	Down,
}

/// <summary>
/// 本机与云端之间的连线，用于呈现同步状态。
///
/// ── 采用图形而非文字 ──────────────────────────────────────────────────────
/// 同步状态由三个变量构成：两端是否存在、版本是否一致、最近一次传输的方向。原实现为
/// 一个文本框，需读完两行文字才能判断一致性；改为连线后三个变量可同时呈现。
///
/// ── 所有视觉属性均编码状态，不含纯装饰 ────────────────────────────────────
///   端点实心 / 空心   该端存档是否存在
///   连线实线 / 虚线   两端版本是否一致
///   两端颜色          左为本机（accent），右为云端（teal），与本窗口其余部分一致
///   高亮段方向        最近一次数据传输的方向
///
/// **虚线转实线**沿用 <see cref="NoriHalo"/> 的约定：在该控件中表示会话已建立，
/// 在此表示版本一致，两处均表示「达成目标状态」，不另行定义表达方式。
///
/// ── 动效仅由用户操作触发 ──────────────────────────────────────────────────
/// 空闲状态为静态。持续循环的动效不携带信息，且在常驻窗口上长期占用注意力资源。
/// 高亮段仅在备份或恢复实际执行时播放一次。
/// </summary>
internal sealed class SyncTether : Control
{
	/// <summary>端点半径。</summary>
	private const double Dot = 4.5;

	/// <summary>高亮段从起点到终点的时长。略长于按钮反馈，以便辨认方向。</summary>
	private static readonly TimeSpan TravelTime = TimeSpan.FromMilliseconds(560);

	private TetherState _state = TetherState.SignedOut;
	private DispatcherTimer? _ticker;
	private TetherDirection _direction;
	private double _progress = -1;   // <0 表示当前无高亮段
	private Action? _onArrived;

	internal SyncTether()
	{
		Height = 26;
		HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
		IsHitTestVisible = false;
	}

	/// <summary>当前状态。</summary>
	internal TetherState State
	{
		get => _state;
		set
		{
			if (_state == value) return;
			_state = value;
			InvalidateVisual();
		}
	}

	/// <summary>
	/// 沿连线播放一段高亮，到达终点时调用 <paramref name="onArrived"/>。
	///
	/// 动效被禁用时跳过播放并直接回调：动效仅为状态变化的附加说明，不属于业务流程，
	/// 缺失时结果仍须送达。
	/// </summary>
	internal void Travel(TetherDirection direction, Action? onArrived = null)
	{
		_direction = direction;
		_onArrived = onArrived;

		if (!MotionPreference.AllowAnimation)
		{
			_progress = -1;
			InvalidateVisual();
			onArrived?.Invoke();
			return;
		}

		_progress = 0;
		_ticker ??= new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(33)};
		_ticker.Tick -= OnTick;
		_ticker.Tick += OnTick;
		_ticker.Start();
		InvalidateVisual();
	}

	/// <summary>停止播放。窗口关闭时必须调用，否则定时器持续运行。</summary>
	internal void Stop()
	{
		_ticker?.Stop();
		_ticker = null;
		_progress = -1;
	}

	private void OnTick(object? sender, EventArgs args)
	{
		_progress += 33.0 / TravelTime.TotalMilliseconds;
		if (_progress >= 1)
		{
			_progress = -1;
			_ticker?.Stop();
			InvalidateVisual();
			Action? arrived = _onArrived;
			_onArrived = null;
			arrived?.Invoke();
			return;
		}
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		double width = Bounds.Width;
		if (width < 40) return;
		double y = Bounds.Height / 2;

		// 连线绘制于两端点之间，不穿过端点圆心：穿过会使端点呈现为线上的节点，
		// 而它们表示连线的两个端。
		double left = Dot * 2;
		double right = width - Dot * 2;

		bool localOk = _state != TetherState.SignedOut;
		bool remoteOk = _state is TetherState.InStep or TetherState.OutOfStep;
		double dim = _state == TetherState.SignedOut ? 0.30 : 1;

		// 线身渐变：本机色 → 云端色，使端点的颜色归属延伸至连线中段。
		LinearGradientBrush line = new()
		{
			StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
			EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
			GradientStops =
			[
				new GradientStop(Tint(ChatPalette.Accent, 0.55 * dim), 0),
				new GradientStop(Tint(ChatPalette.Teal, 0.55 * dim), 1),
			],
		};

		// 仅版本一致时为实线，其余状态为虚线。与 NoriHalo 的虚线/实线约定含义一致。
		ImmutablePen pen = new(line.ToImmutable(), _state == TetherState.InStep ? 1.4 : 1,
			_state == TetherState.InStep ? null : new ImmutableDashStyle([3, 4], 0));
		context.DrawLine(pen, new Point(left, y), new Point(right, y));

		DrawEnd(context, new Point(Dot + 1, y), ChatPalette.Accent, localOk, dim);
		DrawEnd(context, new Point(width - Dot - 1, y), ChatPalette.Teal, remoteOk, dim);

		if (_progress >= 0) DrawSpark(context, left, right, y);
	}

	/// <summary>
	/// 一个端点。
	///
	/// 存在时填充，不存在时仅描边。空心表示「无内容」，比降低不透明度更明确
	/// （后者易被理解为「不可用」）。
	/// </summary>
	private static void DrawEnd(DrawingContext context, Point at, IBrush color, bool solid, double dim)
	{
		if (solid)
		{
			context.DrawEllipse(Tinted(color, dim), null, at, Dot, Dot);
			return;
		}
		context.DrawEllipse(null, new ImmutablePen(Tinted(color, 0.5 * dim).ToImmutable(), 1), at, Dot - 0.5, Dot - 0.5);
	}

	/// <summary>
	/// 沿连线移动的高亮段。
	///
	/// 带拖尾：33ms 的步进下单个点呈现为跳变，拖尾将各帧连为连续轨迹，方向才可辨认。
	/// </summary>
	private void DrawSpark(DrawingContext context, double left, double right, double y)
	{
		double t = _direction == TetherDirection.Up ? _progress : 1 - _progress;
		double head = left + (right - left) * t;
		// 拖尾始终位于运动方向的反侧。
		double tail = head - (right - left) * 0.30 * (_direction == TetherDirection.Up ? 1 : -1);
		IBrush color = _direction == TetherDirection.Up ? ChatPalette.Accent : ChatPalette.Teal;

		// 亮度按正弦分布，终点处趋近 0，无需额外的淡出处理。
		double strength = Math.Sin(_progress * Math.PI);

		/*
		 * 拖尾的视觉权重需高于头部。
		 *
		 * 初版为「高亮点 + 低对比度拖尾」，静帧下呈现为连线中部的第三个点，无法辨认
		 * 方向 —— 而方向是本动效唯一承载的信息。现将拖尾加长加粗、头部缩小。
		 */
		LinearGradientBrush trail = new()
		{
			StartPoint = new RelativePoint(tail, y, RelativeUnit.Absolute),
			EndPoint = new RelativePoint(head, y, RelativeUnit.Absolute),
			GradientStops =
			[
				new GradientStop(Tint(color, 0), 0),
				new GradientStop(Tint(color, strength), 1),
			],
		};
		context.DrawLine(new ImmutablePen(trail.ToImmutable(), 3, lineCap: PenLineCap.Round),
			new Point(tail, y), new Point(head, y));
		context.DrawEllipse(Tinted(color, strength), null, new Point(head, y), 2.2, 2.2);
	}

	private static Color Tint(IBrush brush, double alpha)
	{
		Color color = brush is ISolidColorBrush solid ? solid.Color : Colors.White;
		return Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
	}

	private static ISolidColorBrush Tinted(IBrush brush, double alpha) => new SolidColorBrush(Tint(brush, alpha));
}
