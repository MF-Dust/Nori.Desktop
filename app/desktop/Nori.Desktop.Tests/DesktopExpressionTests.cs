using System.Globalization;
using Avalonia.Media;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Emotion;
using Nori.Core.Expression;
using Nori.Core.Security;
using Nori.Desktop.Expression;

namespace Nori.Desktop.Tests;

/// <summary>
/// 会改持久系统设置的两条表达通道。
///
/// 重点全在**还原**上。改了用户机器上的持久状态就必须有还原路径 —— 这和 AppContainer 的
/// ACL 授权是同一类，那次是写了文档没接调用点，这次从一开始就把它做进来并测到。
///
/// 用假的桌面外观实现，用例不碰真实注册表与壁纸。
/// </summary>
public sealed class DesktopExpressionTests : IDisposable
{
	private sealed class FakeAppearance : IDesktopAppearance
	{
		public bool IsAvailable { get; init; } = true;

		public string? Wallpaper { get; set; } = @"D:\wallpaper\original.jpg";

		public uint? Accent { get; set; } = 0xFFD4C677;   // 实测值：AABBGGRR

		public List<Color> AccentWrites { get; } = [];

		public List<string> WallpaperWrites { get; } = [];

		public string? GetWallpaper() => Wallpaper;

		public void SetWallpaper(string path)
		{
			WallpaperWrites.Add(path);
			Wallpaper = path;
		}

		public uint? GetAccentColor() => Accent;

		public void SetAccentColor(Color color)
		{
			AccentWrites.Add(color);
			Accent = 0xFF000000u | ExpressionColors.ToWindowsBgr(color);
		}
	}

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = [.. Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index)];

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-expr-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly DesktopStateBackup _backup;

	public DesktopExpressionTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("test");
		_backup = new DesktopStateBackup(_config);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	private static ExpressionPalette Palette(string emotion = EmotionTypes.Happy, double intensity = 0.9) =>
		EmotionExpression.For(new EmotionState {Type = emotion, Intensity = intensity, LastUpdated = 0});

	// ---- 字节序 ----

	/// <summary>
	/// 两个注册表键的字节序是相反的，这是在真机上读出来确认的：
	///
	/// <code>
	/// DWM\ColorizationColor = 0xC477C6D4   AARRGGBB
	/// DWM\AccentColor       = 0xFFD4C677   AABBGGRR   ← 同一个颜色
	/// </code>
	///
	/// 写反了不报错，桌面变成补色。
	/// </summary>
	[Fact]
	public void 强调色按BGR字节序()
	{
		Assert.Equal(0x00D4C677u, ExpressionColors.ToWindowsBgr(Color.FromRgb(0x77, 0xC6, 0xD4)));
	}

	// ---- 备份语义 ----

	/// <summary>
	/// 第二次记录不能覆盖。
	///
	/// 这是这一族最容易写错的一条：第二次调用时当前值已经是她改过的，再存一遍就把真正的
	/// 原值永久丢了，用户再也还原不回去。
	/// </summary>
	[Fact]
	public void 重复记录不覆盖原值()
	{
		_backup.Remember("k", "原来的");
		_backup.Remember("k", "她改过的");

		Assert.Equal("原来的", _backup.Original("k"));
	}

	[Fact]
	public void 空值不记录()
	{
		_backup.Remember("k", null);

		Assert.False(_backup.HasBackup("k"));
	}

	[Fact]
	public void 还原后清掉备份()
	{
		_backup.Remember("k", "原来的");
		_backup.Forget("k");

		Assert.Null(_backup.Original("k"));
		Assert.False(_backup.HasBackup("k"));
	}

	/// <summary>
	/// 纯数字的备份值必须能原样读回来。
	///
	/// 配置层会把内容形如数字的文本识别成 Integer，按 ConfigValue.Text 模式匹配就读不回来，
	/// 表现为「存了但还原不了」且没有任何报错。强调色的备份值恰好是纯数字串，第一版就是
	/// 这么错的 —— 这个坑在本仓库已经第四次出现。
	/// </summary>
	[Fact]
	public void 纯数字的备份值能读回来()
	{
		_backup.Remember("k", "4292134519");

		Assert.Equal("4292134519", _backup.Original("k"));
	}

	[Fact]
	public void 路径形式的备份值能读回来()
	{
		_backup.Remember("k", @"D:\wallpaper\img.jpg");

		Assert.Equal(@"D:\wallpaper\img.jpg", _backup.Original("k"));
	}

	// ---- 强调色 ----

	[Fact]
	public async Task 强调色是全局档默认不开()
	{
		AccentColorChannel channel = new(new FakeAppearance(), _backup);

		Assert.Equal(Intrusiveness.Global, channel.Level);
		await Task.CompletedTask;
	}

	[Fact]
	public async Task 改强调色之前先备份原值()
	{
		FakeAppearance appearance = new() {Accent = 0xFFD4C677};
		AccentColorChannel channel = new(appearance, _backup);

		await channel.ApplyAsync(Palette(), CancellationToken.None);

		Assert.Single(appearance.AccentWrites);
		Assert.Equal(
			0xFFD4C677u.ToString(CultureInfo.InvariantCulture),
			_backup.Original(AccentColorChannel.ChannelKey));
	}

	/// <summary>连续下发多次仍只备份最初那一次的值。</summary>
	[Fact]
	public async Task 多次下发不会把改过的值当成原值()
	{
		FakeAppearance appearance = new() {Accent = 0xFFD4C677};
		AccentColorChannel channel = new(appearance, _backup);

		await channel.ApplyAsync(Palette(EmotionTypes.Happy), CancellationToken.None);
		await channel.ApplyAsync(Palette(EmotionTypes.Angry), CancellationToken.None);

		Assert.Equal(
			0xFFD4C677u.ToString(CultureInfo.InvariantCulture),
			_backup.Original(AccentColorChannel.ChannelKey));
	}

	[Fact]
	public async Task 还原把强调色改回原来的颜色()
	{
		FakeAppearance appearance = new() {Accent = 0xFFD4C677};
		AccentColorChannel channel = new(appearance, _backup);
		await channel.ApplyAsync(Palette(), CancellationToken.None);

		channel.Restore();

		// 0xFFD4C677 是 AABBGGRR，拆回来应当是 R=77 G=C6 B=D4。
		Color restored = appearance.AccentWrites[^1];
		Assert.Equal(0x77, restored.R);
		Assert.Equal(0xC6, restored.G);
		Assert.Equal(0xD4, restored.B);
		Assert.False(_backup.HasBackup(AccentColorChannel.ChannelKey));
	}

	[Fact]
	public void 没有备份时还原不做任何事()
	{
		FakeAppearance appearance = new();

		new AccentColorChannel(appearance, _backup).Restore();

		Assert.Empty(appearance.AccentWrites);
	}

	/// <summary>备份值坏了要清掉，否则每次启动都拿它去试一遍。</summary>
	[Fact]
	public void 备份值损坏时清掉而不是反复重试()
	{
		_config.Set(AccentColorChannel.ChannelKey + "_original", new ConfigValue.Text("不是数字"));
		FakeAppearance appearance = new();

		new AccentColorChannel(appearance, _backup).Restore();

		Assert.Empty(appearance.AccentWrites);
		Assert.False(_backup.HasBackup(AccentColorChannel.ChannelKey));
	}

	[Fact]
	public async Task 平台不可用时强调色通道不动手()
	{
		FakeAppearance appearance = new() {IsAvailable = false};
		AccentColorChannel channel = new(appearance, _backup);

		await channel.ApplyAsync(Palette(), CancellationToken.None);

		Assert.False(channel.IsAvailable);
		Assert.Empty(appearance.AccentWrites);
	}

	// ---- 壁纸 ----

	[Fact]
	public void 壁纸是全局档()
	{
		Assert.Equal(
			Intrusiveness.Global,
			new WallpaperChannel(new FakeAppearance(), _backup, Path.Combine(Path.GetTempPath(), "x.jpg")).Level);
	}

	[Fact]
	public void 还原把壁纸改回原来那张()
	{
		FakeAppearance appearance = new() {Wallpaper = @"D:\wallpaper\original.jpg"};
		string original = Path.Combine(Path.GetTempPath(), $"nori-wp-{Guid.NewGuid():N}.jpg");
		File.WriteAllBytes(original, [1, 2, 3]);
		appearance.Wallpaper = original;

		WallpaperChannel channel = new(appearance, _backup, Path.Combine(Path.GetTempPath(), "generated.jpg"));
		_backup.Remember(WallpaperChannel.ChannelKey, appearance.GetWallpaper());
		appearance.Wallpaper = "generated.jpg";

		channel.Restore();

		Assert.Equal(original, appearance.WallpaperWrites[^1]);
		Assert.False(_backup.HasBackup(WallpaperChannel.ChannelKey));
		File.Delete(original);
	}

	/// <summary>原图已经被用户删掉时不要去设一个不存在的路径 —— 那会让桌面变成纯黑。</summary>
	[Fact]
	public void 原壁纸文件已不存在时不设置()
	{
		FakeAppearance appearance = new();
		_backup.Remember(WallpaperChannel.ChannelKey, @"D:\wallpaper\已经删掉了.jpg");

		new WallpaperChannel(appearance, _backup, Path.Combine(Path.GetTempPath(), "g.jpg")).Restore();

		Assert.Empty(appearance.WallpaperWrites);
	}

	// ---- 灯效（A6）----

	/// <summary>
	/// 控制器数据解析必须防御式。
	///
	/// 协议实现未经真实服务端验证，因此任何字段不一致都要把设备判为不可用而不是拿错值去写
	/// 硬件。最坏情况是「没检测到设备」，不是「发了乱七八糟的数据给键盘」。
	/// </summary>
	[Theory]
	[InlineData(new byte[0])]
	[InlineData(new byte[] {1, 2, 3})]
	[InlineData(new byte[] {0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF})]   // 字符串长度远超载荷
	public void 控制器数据损坏时判为不可用(byte[] payload)
	{
		Assert.Null(OpenRgbClient.TryParseDevice(payload, 0));
	}

	[Fact]
	public void 连不上时通道不可用()
	{
		using RgbLightingChannel channel = new(() => null);

		Assert.False(channel.IsAvailable);
		Assert.Empty(channel.Devices);
	}

	/// <summary>
	/// 灯效是 Peripheral 不是 Global：它不改屏幕上的任何东西，而装了 OpenRGB 的人本来就是
	/// 主动要灯效的，默认开是合理的。
	/// </summary>
	[Fact]
	public void 灯效是外围档()
	{
		using RgbLightingChannel channel = new(() => null);

		Assert.Equal(Intrusiveness.Peripheral, channel.Level);
	}

	[Fact]
	public async Task 没有设备时下发不抛()
	{
		using RgbLightingChannel channel = new(() => null);

		await channel.ApplyAsync(Palette(), CancellationToken.None);
	}

	// ---- 环境音（A3 壳）----

	[Fact]
	public void 没有素材目录时环境音不可用()
	{
		AmbientSoundChannel channel = new(Path.Combine(Path.GetTempPath(), $"nori-none-{Guid.NewGuid():N}"));

		Assert.False(channel.IsAvailable);
		Assert.Empty(channel.Available());
	}

	[Fact]
	public void 有素材时按音景名解析出文件()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"nori-snd-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		File.WriteAllBytes(Path.Combine(directory, "calm.ogg"), [1]);
		File.WriteAllBytes(Path.Combine(directory, "readme.txt"), [1]);
		try
		{
			AmbientSoundChannel channel = new(directory);

			Assert.True(channel.IsAvailable);
			Assert.Equal(["calm"], channel.Available());       // 非音频扩展名不算
			Assert.EndsWith("calm.ogg", channel.Resolve("calm")!, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>
	/// 缺素材时不要拿别的音景顶上。
	///
	/// 随便挑一个比不放更糟：用户会听到与当下情绪不符的声音，而且无从判断是配错了还是缺文件。
	/// </summary>
	[Fact]
	public void 缺这个音景时不退回到别的()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"nori-snd-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		File.WriteAllBytes(Path.Combine(directory, "calm.ogg"), [1]);
		try
		{
			Assert.Null(new AmbientSoundChannel(directory).Resolve("tense"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
