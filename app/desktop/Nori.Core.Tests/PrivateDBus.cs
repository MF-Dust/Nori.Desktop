using System.Diagnostics;

namespace Nori.Core.Tests;

/// <summary>仅在 Linux 且可找到 dbus-daemon 时运行；测试使用私有总线，不占用桌面会话的真实服务。</summary>
public sealed class PrivateDBusFactAttribute : FactAttribute
{
	public PrivateDBusFactAttribute()
	{
		if (PrivateDBus.DaemonPath is null) Skip = "需要 Linux 与 dbus-daemon";
	}
}

/// <summary>测试专用的私有 dbus-daemon：可声明按需启动的服务，释放时回收整个进程树与临时目录。</summary>
internal sealed class PrivateDBus : IAsyncDisposable
{
	private readonly Process _daemon;
	private int _disposed;

	private PrivateDBus(Process daemon, string directory, string address)
	{
		_daemon = daemon;
		Directory = directory;
		Address = address;
	}

	/// <summary>总线地址，可直接交给 DBusConnection。</summary>
	internal string Address { get; }

	/// <summary>本总线独占的临时目录，测试可在此放置标记文件。</summary>
	internal string Directory { get; }

	internal static string? DaemonPath => OperatingSystem.IsLinux()
		? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(directory => System.IO.Path.Combine(directory, "dbus-daemon"))
			.FirstOrDefault(File.Exists)
		: null;

	/// <param name="activatable">按总线目录生成「服务名 → Exec」；总线收到发往这些名称的调用时会拉起对应进程。</param>
	internal static async Task<PrivateDBus> StartAsync(Func<string, IReadOnlyDictionary<string, string>>? activatable = null)
	{
		string daemonPath = DaemonPath ?? throw new InvalidOperationException("未找到 dbus-daemon");
		string directory = System.IO.Directory.CreateTempSubdirectory("nori-dbus-").FullName;
		string services = System.IO.Path.Combine(directory, "services");
		System.IO.Directory.CreateDirectory(services);
		foreach ((string name, string exec) in activatable?.Invoke(directory) ?? new Dictionary<string, string>())
			File.WriteAllText(System.IO.Path.Combine(services, $"{name}.service"), $"[D-BUS Service]\nName={name}\nExec={exec}\n");
		string config = System.IO.Path.Combine(directory, "bus.conf");
		File.WriteAllText(config, $"""
			<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
			 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
			<busconfig>
			  <type>session</type>
			  <listen>unix:dir={directory}</listen>
			  <servicedir>{services}</servicedir>
			  <policy context="default">
			    <allow send_destination="*" eavesdrop="true"/>
			    <allow eavesdrop="true"/>
			    <allow own="*"/>
			  </policy>
			</busconfig>
			""");

		ProcessStartInfo start = new(daemonPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		start.ArgumentList.Add("--nofork");
		start.ArgumentList.Add("--print-address=1");
		start.ArgumentList.Add($"--config-file={config}");
		Process daemon = Process.Start(start) ?? throw new InvalidOperationException("无法启动 dbus-daemon");
		List<string> errors = [];
		daemon.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (errors) errors.Add(line.Data); };
		daemon.BeginErrorReadLine();
		try
		{
			string? address = await daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
			if (string.IsNullOrWhiteSpace(address))
			{
				lock (errors) throw new InvalidOperationException($"dbus-daemon 未输出地址：{string.Join('\n', errors)}");
			}
			return new PrivateDBus(daemon, directory, address.Trim());
		}
		catch
		{
			Stop(daemon);
			System.IO.Directory.Delete(directory, true);
			throw;
		}
	}

	public ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
		Stop(_daemon);
		// 被拉起的服务进程可能已脱离进程树并继承了输出管道，不能等待管道读到结尾。
		_daemon.WaitForExit(TimeSpan.FromSeconds(5));
		_daemon.CancelErrorRead();
		_daemon.Dispose();
		try { System.IO.Directory.Delete(Directory, true); }
		catch (IOException) { /* 服务进程可能刚好还在写标记，临时目录留给系统清理。 */ }
		return ValueTask.CompletedTask;
	}

	/// <summary>连同总线拉起的服务进程一起结束。</summary>
	private static void Stop(Process daemon)
	{
		try { daemon.Kill(entireProcessTree: true); }
		catch (InvalidOperationException) { /* 进程已经退出。 */ }
	}
}
