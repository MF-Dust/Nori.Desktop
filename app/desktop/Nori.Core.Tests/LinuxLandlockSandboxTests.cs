using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Nori.Core.Sandbox;
using Nori.Core.Sandbox.Linux;

namespace Nori.Core.Tests;

public sealed class LinuxLandlockSandboxTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("nori-landlock-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Theory]
	[InlineData(1, false, 0x1FFFUL, 0UL)]
	[InlineData(2, false, 0x3FFFUL, 0UL)]
	[InlineData(3, false, 0x7FFFUL, 0UL)]
	[InlineData(4, false, 0x7FFFUL, 3UL)]
	[InlineData(4, true, 0x7FFFUL, 0UL)]
	[InlineData(11, false, 0x7FFFUL, 3UL)]
	public void LandlockAbi_仅处理已知且内核版本支持的权限位(int abi, bool allowNetwork, ulong files, ulong network)
	{
		Assert.Equal((files, network), LandlockAbi.BuildAccessMasks(abi, allowNetwork));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(38)]
	[InlineData(95)]
	public void SandboxLauncherFactory_Landlock系统调用不可用时退回无隔离(int error)
	{
		int abi = LandlockAbi.UnavailableOrThrow(error);

		Assert.IsType<UnsandboxedLauncher>(SandboxLauncherFactory.CreateLinux(abi));
	}

	[Fact]
	public void LandlockAbi_不会将其他探测错误视为无隔离()
	{
		Assert.Equal(22, Assert.Throws<Win32Exception>(() => LandlockAbi.UnavailableOrThrow(22)).NativeErrorCode);
	}

	[Fact]
	public async Task LinuxLandlockLauncher_限定读写路径且不限制宿主()
	{
		if (LandlockAbi.Probe() == 0) return;

		string outside = Path.Combine(_root, "outside.txt");
		string readable = Path.Combine(_root, "readable.txt");
		await File.WriteAllTextAsync(outside, "outside-content");
		await File.WriteAllTextAsync(readable, "read-only-content");
		SandboxPolicy policy = Policy() with { ReadOnlyPaths = [readable, Path.Combine(_root, "missing")] };
		ISandboxLauncher launcher = SandboxLauncherFactory.Create();

		SandboxResult inside = await launcher.RunAsync(
			"/bin/sh -c \"printf '%s' \\\"$NORI_SANDBOX_PROBE\\\" > inside; cat inside; printf ':stderr' >&2\"",
			policy, CancellationToken.None);
		Assert.Equal(SandboxIsolation.Landlock, launcher.Isolation);
		Assert.Equal(0, inside.ExitCode);
		Assert.Equal("probe-value:stderr", inside.Output);
		Assert.Equal("probe-value", await File.ReadAllTextAsync(Path.Combine(policy.WorkspaceRoot, "inside")));

		SandboxResult readOutside = await launcher.RunAsync($"/bin/cat \"{outside}\"", policy, CancellationToken.None);
		Assert.NotEqual(0, readOutside.ExitCode);
		Assert.Contains("Permission denied", readOutside.Output);
		Assert.DoesNotContain("outside-content", readOutside.Output);

		SandboxResult readAllowed = await launcher.RunAsync($"/bin/cat \"{readable}\"", policy, CancellationToken.None);
		Assert.Equal(0, readAllowed.ExitCode);
		Assert.Equal("read-only-content", readAllowed.Output);

		foreach (string path in new[] { outside, readable })
		{
			SandboxResult write = await launcher.RunAsync($"/bin/sh -c \"echo escaped >> '{path}'\"", policy, CancellationToken.None);
			Assert.NotEqual(0, write.ExitCode);
			Assert.Contains("Permission denied", write.Output);
		}
		Assert.Equal("read-only-content", await File.ReadAllTextAsync(readable));
		Assert.Equal("outside-content", await File.ReadAllTextAsync(outside));
		await File.AppendAllTextAsync(outside, ":host-can-write");
		Assert.Equal("outside-content:host-can-write", await File.ReadAllTextAsync(outside));
	}

	[Theory]
	[InlineData(128)]
	[InlineData(8)]
	[InlineData(0)]
	public async Task LinuxLandlockLauncher_同时限制输出长度和保留合并后的首尾(int limit)
	{
		if (LandlockAbi.Probe() == 0) return;

		SandboxResult result = await SandboxLauncherFactory.Create().RunAsync(
			"/bin/sh -c \"printf 'head%100000s' x; printf tail >&2\"",
			Policy() with { MaxOutputCharacters = limit }, CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.Truncated);
		Assert.True(result.Output.Length <= limit);
		if (limit == 0) Assert.Empty(result.Output);
		else
		{
			Assert.StartsWith("head", result.Output);
			Assert.EndsWith("tail", result.Output);
		}
	}

	[Fact]
	public async Task LinuxLandlockLauncher_超时终止组长退出后仍持有输出的后代()
	{
		if (LandlockAbi.Probe() == 0) return;

		SandboxPolicy policy = Policy() with { Timeout = TimeSpan.FromSeconds(1) };
		SandboxResult result = await SandboxLauncherFactory.Create().RunAsync(
			"/bin/sh -c \"(printf started; sleep 2; echo survived > survived) &\"", policy, CancellationToken.None);

		Assert.True(result.TimedOut);
		Assert.Equal(-1, result.ExitCode);
		Assert.Contains("started", result.Output);
		await Task.Delay(TimeSpan.FromSeconds(2));
		Assert.False(File.Exists(Path.Combine(policy.WorkspaceRoot, "survived")));
	}

	[Fact]
	public async Task LinuxLandlockLauncher_在ABI四以上按策略拒绝或允许TCP连接()
	{
		if (LandlockAbi.Probe() < 4 || !File.Exists("/bin/bash")) return;

		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		string command = $"/bin/bash -c \"echo test >/dev/tcp/127.0.0.1/{port}\"";
		SandboxPolicy policy = Policy();
		ISandboxLauncher launcher = SandboxLauncherFactory.Create();

		SandboxResult blocked = await launcher.RunAsync(command, policy, CancellationToken.None);
		Assert.NotEqual(0, blocked.ExitCode);
		Assert.Contains("Permission denied", blocked.Output);
		Assert.False(listener.Pending());

		SandboxResult allowed = await launcher.RunAsync(command, policy with { AllowNetwork = true }, CancellationToken.None);
		Assert.Equal(0, allowed.ExitCode);
		Assert.True(listener.Pending());
	}

	private SandboxPolicy Policy() => new()
	{
		WorkspaceRoot = Directory.CreateDirectory(Path.Combine(_root, "working folder")).FullName,
		EnvironmentOverrides = new Dictionary<string, string> { ["LC_ALL"] = "C", ["NORI_SANDBOX_PROBE"] = "probe-value" },
		Timeout = TimeSpan.FromSeconds(10),
	};
}
