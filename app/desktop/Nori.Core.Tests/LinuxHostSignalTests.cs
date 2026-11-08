using Nori.Core.Platform;
using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

/// <summary>Linux 内存、CPU、电源、空闲和动效的解析，不连接用户桌面。</summary>
public sealed class LinuxHostSignalTests
{
	[Fact]
	public void 内存按可用量计算已用且矛盾时不编造()
	{
		(int? used, int? total) = LinuxProcStats.ParseMeminfo("""
			MemTotal:       16000000 kB
			MemFree:         1000000 kB
			MemAvailable:    8000000 kB
			""");
		Assert.Equal(7812, used);
		Assert.Equal(15625, total);

		(used, total) = LinuxProcStats.ParseMeminfo("MemTotal:       16000000 kB\r\nMemAvailable:    8000000 kB\r\n");
		Assert.Equal(7812, used);
		Assert.Equal(15625, total);

		(used, total) = LinuxProcStats.ParseMeminfo("MemTotal: 2048 kB\n");
		Assert.Null(used);
		Assert.Equal(2, total);

		Assert.Equal((null, null), LinuxProcStats.ParseMeminfo("MemTotal: 8192 kB\nMemAvailable: 9000 kB\n"));
		Assert.Equal((null, null), LinuxProcStats.ParseMeminfo("MemTotal: 1000 MB\n"));
	}

	[Fact]
	public void 本机内存总量可读()
	{
		if (!OperatingSystem.IsLinux()) return;
		(int? _, int? total) = LinuxProcStats.ReadMemory();
		Assert.NotNull(total);
		Assert.True(total > 0);
	}

	[Fact]
	public void CPU汇总不把guest再加一次且第一次没有基线()
	{
		Assert.True(LinuxProcStats.TryParseCpu("cpu  100 0 0 0 0 0 0 0 50 0\ncpu0 1 0 0 0 0 0 0 0\n", out LinuxProcStats.CpuTimes times));
		Assert.Equal(0ul, times.Idle);
		Assert.Equal(100ul, times.Total);

		Assert.True(LinuxProcStats.TryParseCpu("cpu 100 0 100 700 100 0 0 0\n", out LinuxProcStats.CpuTimes next));
		Assert.Equal(800ul, next.Idle);
		Assert.Equal(1000ul, next.Total);
		Assert.Null(LinuxProcStats.Percent(default, next));
		Assert.Equal(20, LinuxProcStats.Percent(new LinuxProcStats.CpuTimes(800, 1000), new LinuxProcStats.CpuTimes(880, 1100)));
		Assert.False(LinuxProcStats.TryParseCpu("cpu0 1 2 3 4\n", out _));
	}

	[Fact]
	public void 外接电源在线时不算电池()
	{
		Assert.False(LinuxPowerSupply.UseBattery([]));
		Assert.False(LinuxPowerSupply.UseBattery(
		[
			new("Mains", null, "1"),
			new("Battery", "Discharging", null),
		]));
		Assert.True(LinuxPowerSupply.UseBattery([new("Battery", "Discharging", null)]));
		Assert.True(LinuxPowerSupply.UseBattery([new("Mains", null, "0"), new("Battery", "Full", null)]));
		Assert.False(LinuxPowerSupply.UseBattery([new("Battery", "Charging", null)]));
		Assert.False(LinuxPowerSupply.UseBattery([new("USB", null, "1")]));
	}

	[Fact]
	public void 空闲毫秒与logind时间戳()
	{
		Assert.Equal(0, LinuxIdleTime.FromMilliseconds(0));
		Assert.Equal(1.5, LinuxIdleTime.FromMilliseconds(1500));
		Assert.Equal(0, LinuxIdleTime.FromLogind(false, 0, 5_000_000));
		Assert.Equal(2.5, LinuxIdleTime.FromLogind(true, 1_000_000, 3_500_000));
		Assert.Null(LinuxIdleTime.FromLogind(true, 0, 3_500_000));
		Assert.Null(LinuxIdleTime.FromLogind(true, 9, 3));
	}

	[Fact]
	public void 变体布尔可以解开一层包装()
	{
		Assert.True(LinuxIdleTime.UnwrapBool(true));
		Assert.False(LinuxIdleTime.UnwrapBool(VariantValue.Variant(false)));
		Assert.Null(LinuxIdleTime.UnwrapBool(VariantValue.String("true")));
		Assert.Equal(9ul, LinuxIdleTime.UnwrapUInt64(VariantValue.Variant(9ul)));
	}

	[Fact]
	public void 动效开关门户优先于配置文件()
	{
		Assert.True(LinuxMotionPreference.SelectAnimations(true, false, false));
		Assert.False(LinuxMotionPreference.SelectAnimations(null, false, true));
		Assert.True(LinuxMotionPreference.SelectAnimations(null, null, true));
		Assert.Null(LinuxMotionPreference.SelectAnimations(null, null, null));
		Assert.Equal(LinuxMotionPreference.DesktopFamily.Kde, LinuxMotionPreference.ClassifyDesktop("KDE", "gnome"));
		Assert.Equal(LinuxMotionPreference.DesktopFamily.Gnome, LinuxMotionPreference.ClassifyDesktop("ubuntu:GNOME", "plasma"));
		Assert.Equal(LinuxMotionPreference.DesktopFamily.Kde, LinuxMotionPreference.ClassifyDesktop(null, "plasma"));
		Assert.False(LinuxMotionPreference.SelectAnimations(null, true, false, LinuxMotionPreference.DesktopFamily.Kde));
		Assert.True(LinuxMotionPreference.SelectAnimations(null, true, false, LinuxMotionPreference.DesktopFamily.Gnome));
		Assert.False(LinuxMotionPreference.SelectAnimations(null, true, false));
		Assert.True(LinuxMotionPreference.PrefersReduced(null));
		Assert.True(LinuxMotionPreference.PrefersReduced(false));
		Assert.False(LinuxMotionPreference.PrefersReduced(true));
		Assert.True(LinuxMotionPreference.AllowDecorative(null));
		Assert.False(LinuxMotionPreference.AllowDecorative(false));

		Assert.Equal(Path.Combine("/home/me", ".config"), LinuxMotionPreference.ResolveConfigHome("relative", "/home/me"));
		Assert.Equal("/xdg", LinuxMotionPreference.ResolveConfigHome("/xdg", "/home/me"));

		Assert.False(LinuxMotionPreference.ParseGtkEnableAnimations("[Settings]\n# note\ngtk-enable-animations=0\n"));
		Assert.True(LinuxMotionPreference.ParseGtkEnableAnimations("gtk-enable-animations=yes\n"));
		Assert.Null(LinuxMotionPreference.ParseGtkEnableAnimations("gtk-enable-animations=maybe\n"));

		const string kde = "[General]\nAnimationDurationFactor=1\n\n[KDE]\nAnimationDurationFactor=0\n";
		Assert.False(LinuxMotionPreference.ParseKdeAnimationFactor(kde));
		Assert.Null(LinuxMotionPreference.ParseKdeAnimationFactor("[General]\nAnimationDurationFactor=0\n"));

		const string configHome = "/cfg";
		Dictionary<string, string> files = new(StringComparer.Ordinal)
		{
			[Path.Combine(configHome, "gtk-4.0", "settings.ini")] = "gtk-enable-animations=maybe\n",
			[Path.Combine(configHome, "gtk-3.0", "settings.ini")] = "gtk-enable-animations=false\n",
			[Path.Combine(configHome, "kdeglobals")] = "[KDE]\nAnimationDurationFactor=1.5\n",
		};
		string? Read(string path) => files.TryGetValue(path, out string? text) ? text : null;
		Assert.False(LinuxMotionPreference.ReadGtkAnimations(Read, configHome));
		Assert.True(LinuxMotionPreference.ReadKdeAnimations(Read, configHome));
	}

	[Fact]
	public async Task X11屏保空闲若能读到则落在合理范围()
	{
		if (!OperatingSystem.IsLinux()) return;
		double? idle = await LinuxIdleTime.ProbeAsync();
		if (idle is null) return;
		Assert.InRange(idle.Value, 0, TimeSpan.FromDays(30).TotalSeconds);
	}
}
