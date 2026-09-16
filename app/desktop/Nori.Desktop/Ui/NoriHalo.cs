using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Ui;

/// <summary>光环的显示档位。只影响颜色与不透明度，不改变结构。</summary>
internal enum HaloMood
{
	/// <summary>空闲：虚线压暗，光晕关闭。未登录且条件不满足时使用。</summary>
	Dormant,

	/// <summary>就绪：虚线转为强调色，标志全亮，光晕半开。</summary>
	Waking,

	/// <summary>处理中：双环反向旋转、标志缩放、光晕脉动。启动画面使用这一档。</summary>
	Working,

	/// <summary>已连接：外环转为青绿实线，光晕全开。</summary>
	Connected,
}

/// <summary>
/// Nori 的品牌标记：四瓣标志 + 两圈虚线 + 一层光晕。
///
/// 从启动画面抽出共用。它是品牌标记而非单个窗口的装饰元素，保留两份实现会在更换
/// 标志或调整配色时产生不一致，且不会有任何报错。
///
/// 同时承担状态指示：<see cref="Mood"/> 决定当前档位。账户窗口用它表示登录状态，
/// 启动画面用它表示初始化状态，语义不同但复用同一套动画。
/// </summary>
internal sealed class NoriHalo : Panel
{
	private const double LogoRatio = 0.56;

	private readonly Ellipse _outerRing;
	private readonly Ellipse _innerRing;
	private readonly Ellipse _glow;
	private readonly Image _logo;

	private readonly RotateTransform _outerRotation = new();
	private readonly RotateTransform _innerRotation = new();
	private readonly ScaleTransform _breathe = new(1, 1);

	private DispatcherTimer? _ticker;
	private double _phase;
	private HaloMood _mood = HaloMood.Working;

	/*
	 * 档位切换采用补间，不直接赋值。
	 *
	 * 原 ApplyMood 为直接赋值，四个档位各为一组定值。初始化窗口需按
	 * Dormant → Waking → Working 依次切换，直接赋值时每一步均为突变。因此拆为
	 * 「当前值」与「目标值」两组，由已有的 33ms 定时器推进补间。
	 *
	 * **定时器未运行时仍直接赋值**：此时无人推进补间，保留一个无法到达的目标值
	 * 会使控件长期停在中间状态。
	 */
	private Look _current;
	private Look _target;

	/// <summary>一个档位中可补间的数值。虚线与否属于结构属性，不在此列。</summary>
	private struct Look
	{
		public Color Stroke;
		public double Thickness;
		public double Outer;
		public double Inner;
		public double Glow;
		public double Logo;
	}

	/// <param name="size">外环直径。logo 按比例跟着走。</param>
	internal NoriHalo(double size)
	{
		Width = size;
		Height = size;

		_outerRing = new Ellipse
		{
			Width = size, Height = size,
			Stroke = ChatPalette.Accent, StrokeThickness = 1, StrokeDashArray = [4, 6],
			Opacity = 0.40, RenderTransform = _outerRotation,
			RenderTransformOrigin = RelativePoint.Center,
		};
		_innerRing = new Ellipse
		{
			Width = size - 26, Height = size - 26,
			Stroke = ChatPalette.Teal, StrokeThickness = 1, StrokeDashArray = [1, 5],
			Opacity = 0.30, RenderTransform = _innerRotation,
			RenderTransformOrigin = RelativePoint.Center,
		};
		_glow = new Ellipse
		{
			Width = size - 52, Height = size - 52,
			Fill = new RadialGradientBrush
			{
				GradientStops =
				[
					new GradientStop(Color.Parse("#3a7de3ff"), 0),
					new GradientStop(Color.Parse("#1a7de3ff"), 0.45),
					new GradientStop(Colors.Transparent, 0.75),
				],
			},
		};
		_logo = new Image
		{
			Width = size * LogoRatio, Height = size * LogoRatio,
			Stretch = Stretch.Uniform,
			RenderTransform = _breathe,
			RenderTransformOrigin = RelativePoint.Center,
			Source = LoadLogo(),
		};

		Children.Add(_outerRing);
		Children.Add(_innerRing);
		Children.Add(_glow);
		Children.Add(_logo);

		// 显式调用一次：否则初始外观取自上面的内联赋值，而 Mood 的默认值是 Working，
		// 同一组数值存在两份，修改其中一份不会报错但会产生不一致。
		ApplyMood();
	}

	/// <summary>
	/// 当前档位。
	///
	/// 只影响颜色与不透明度；是否旋转由 <see cref="Start"/> / <see cref="Stop"/> 控制。
	/// 两者分离是因为「静止且高亮」与「旋转且压暗」都是需要的组合。
	/// </summary>
	internal HaloMood Mood
	{
		get => _mood;
		set
		{
			if (_mood == value) return;
			_mood = value;
			ApplyMood();
		}
	}

	/// <summary>开始旋转。</summary>
	internal void Start()
	{
		if (_ticker is not null) return;
		_ticker = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(33)};
		_ticker.Tick += (_, _) =>
		{
			_phase += 0.033;
			// 两圈反向转，周期取原来 CSS 上的 14s 与 22s。
			_outerRotation.Angle = _phase / 14 * 360 % 360;
			_innerRotation.Angle = 360 - _phase / 22 * 360 % 360;
			// 呼吸：4 秒一个来回，幅度 3%。
			double scale = 1 + 0.03 * Math.Sin(_phase / 4 * 2 * Math.PI);
			_breathe.ScaleX = scale;
			_breathe.ScaleY = scale;
			/*
			 * 档位切换的补间速率。
			 *
			 * 0.25 对应约 120ms 收敛。取值依据：初始化窗口每档停留 260ms，若补间时长
			 * 与停留时长相当（初版取 0.12，约 250ms），各档位在到达目标前即被切换，
			 * 整段呈现为连续渐变。补间快于停留时长，各档位才能分别呈现。
			 */
			Step(0.25);
			// 脉动在补间之后执行：它需要覆盖 Paint 写入的基准值。
			if (_mood is HaloMood.Working) _glow.Opacity = 0.65 + 0.25 * Math.Sin(_phase / 3 * 2 * Math.PI);
		};
		_ticker.Start();
	}

	/// <summary>停止旋转。定时器必须真正停止，避免后台空转。</summary>
	internal void Stop()
	{
		_ticker?.Stop();
		_ticker = null;
	}

	/// <summary>
	/// 切到已连接档位。
	///
	/// 外环由虚线转为青绿实线，光晕与标志全亮。使用一次性属性赋值而非补间动画：
	/// 该帧之后窗口即关闭，补间会被中途打断。
	/// </summary>
	internal void Connect()
	{
		Mood = HaloMood.Connected;
		// 引入补间后这两行是必需的：定时器运行时 Mood 仅设置目标值，而本帧之后窗口
		// 即关闭，补间无法完成 —— 即本方法注释所述的情况。
		_current = _target;
		Paint();
	}

	/// <summary>
	/// 计算目标档位的取值。
	///
	/// 虚线与否在此直接设置：它是结构属性，不参与补间；且**虚线转实线**是 Connected
	/// 档唯一的形状变化，需要无过渡地切换。
	/// </summary>
	private void ApplyMood()
	{
		_target = LookOf(_mood);
		_outerRing.StrokeDashArray = _mood == HaloMood.Connected ? null : [4, 6];

		// 无人推进补间时直接赋值，理由见字段上的说明。
		if (_ticker is null)
		{
			_current = _target;
			Paint();
		}
	}

	/// <summary>将当前值向目标值推进一帧。<paramref name="k"/> 为本帧消除的差值比例。</summary>
	private void Step(double k)
	{
		_current.Stroke = Blend(_current.Stroke, _target.Stroke, k);
		_current.Thickness += (_target.Thickness - _current.Thickness) * k;
		_current.Outer += (_target.Outer - _current.Outer) * k;
		_current.Inner += (_target.Inner - _current.Inner) * k;
		_current.Glow += (_target.Glow - _current.Glow) * k;
		_current.Logo += (_target.Logo - _current.Logo) * k;
		Paint();
	}

	private void Paint()
	{
		_outerRing.Stroke = new SolidColorBrush(_current.Stroke);
		_outerRing.StrokeThickness = _current.Thickness;
		_outerRing.Opacity = _current.Outer;
		_innerRing.Opacity = _current.Inner;
		_glow.Opacity = _current.Glow;
		_logo.Opacity = _current.Logo;
	}

	private static Color Blend(Color from, Color to, double k) => Color.FromArgb(
		(byte)(from.A + (to.A - from.A) * k),
		(byte)(from.R + (to.R - from.R) * k),
		(byte)(from.G + (to.G - from.G) * k),
		(byte)(from.B + (to.B - from.B) * k));

	private static Color ColorOf(IBrush brush) =>
		brush is ISolidColorBrush solid ? solid.Color : Colors.White;

	private Look LookOf(HaloMood mood)
	{
		switch (mood)
		{
			/*
			 * 各档之间需要可辨识的对比度。
			 *
			 * 初版取 0.28 / 0.42，在该底色上视觉差异不足，状态指示因此失效。
			 * 现取值：空闲为压暗的中性灰，就绪为强调色，处理中进一步提亮。
			 */
			case HaloMood.Dormant:
				return new Look
				{
					Stroke = ColorOf(ChatPalette.Faint), Thickness = 1,
					Outer = 0.22, Inner = 0.12, Glow = 0.06, Logo = 0.30,
				};

			case HaloMood.Waking:
				return new Look
				{
					Stroke = ColorOf(ChatPalette.Accent), Thickness = 1.2,
					Outer = 0.8, Inner = 0.45, Glow = 0.55, Logo = 1,
				};

			case HaloMood.Connected:
				return new Look
				{
					Stroke = ColorOf(ChatPalette.Teal), Thickness = 1.5,
					Outer = 0.95, Inner = 0.5, Glow = 1, Logo = 1,
				};

			default:
				// Working 的光晕由定时器里的脉动接管，这里给的是它的基准值。
				return new Look
				{
					Stroke = ColorOf(ChatPalette.Accent), Thickness = 1,
					Outer = 0.40, Inner = 0.30, Glow = 0.65, Logo = 1,
				};
		}
	}

	/// <summary>标志位图。读取失败时不绘制，窗口其余部分仍可用。</summary>
	private static Bitmap? LoadLogo()
	{
		try
		{
			return new Bitmap(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/logo.png")));
		}
		catch
		{
			return null;
		}
	}
}
