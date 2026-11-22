using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nori.Core.Sandbox.Linux;

internal sealed class LinuxLandlockLauncher(int abi) : ISandboxLauncher
{
	private const int KillSignal = 9;
	private const int NoSuchProcess = 3;
	private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(250);
	private static readonly string HostPath = Path.Combine(AppContext.BaseDirectory, "Nori.SandboxHost");

	internal static int AvailableAbi => OperatingSystem.IsLinux() && File.Exists(HostPath) ? LandlockAbi.Probe() : 0;

	public SandboxIsolation Isolation => SandboxIsolation.Landlock;

	public async Task<SandboxResult> RunAsync(string commandLine, SandboxPolicy policy, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		(string fileName, string arguments) = CommandLine.Split(commandLine);
		ProcessStartInfo start = new(HostPath, arguments)
		{
			WorkingDirectory = AppContext.BaseDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		SandboxHostRequest request = new(abi, fileName, policy.WorkspaceRoot, policy.ReadOnlyPaths,
			policy.AllowNetwork, policy.EnvironmentOverrides);

		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(policy.Timeout);
		using Process process = Process.Start(start) // nosemgrep
			?? throw new InvalidOperationException("无法启动 Linux 沙箱辅助进程。");
		using CancellationTokenSource drain = new();
		OutputBuffer output = new(policy.MaxOutputCharacters);
		Task read = Task.WhenAll(output.ReadAsync(process.StandardOutput, drain.Token),
			output.ReadAsync(process.StandardError, drain.Token));
		Task run = Task.WhenAll(read, SendRequestAsync(process, request, deadline.Token), process.WaitForExitAsync());
		try
		{
			await run.WaitAsync(deadline.Token).ConfigureAwait(false);
			return output.Result(process.ExitCode, timedOut: false);
		}
		catch (OperationCanceledException) when (deadline.IsCancellationRequested)
		{
			TerminateGroup(process);
			drain.CancelAfter(OutputDrainTimeout);
			try
			{
				await read.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (drain.IsCancellationRequested)
			{
				// 留在进程组之外的进程也可能持有输出管道；不能让它无限拖住超时返回。
			}
			cancellationToken.ThrowIfCancellationRequested();
			return output.Result(-1, timedOut: true);
		}
	}

	public void Release(SandboxPolicy policy) { }

	private static async Task SendRequestAsync(Process process, SandboxHostRequest request, CancellationToken cancellationToken)
	{
		using StreamWriter input = process.StandardInput;
		await JsonSerializer.SerializeAsync(input.BaseStream, request, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private static void TerminateGroup(Process process)
	{
		// 即使组长已退出，也要结束仍在组内的常规后代；主动新建会话或脱组的进程不在此范围内。
		if (Kill(-process.Id, KillSignal) == 0) return;
		int error = Marshal.GetLastPInvokeError();
		if (error != NoSuchProcess) throw new Win32Exception(error, "无法终止 Linux 沙箱任务进程组。");

		// 极短的超时可能先于辅助进程建立进程组。
		try
		{
			process.Kill();
		}
		catch (InvalidOperationException) when (process.HasExited) { }
		if (Kill(-process.Id, KillSignal) != 0 && Marshal.GetLastPInvokeError() != NoSuchProcess)
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法终止 Linux 沙箱任务进程组。");
	}

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static extern int Kill(int pid, int signal);

	private sealed class OutputBuffer(int limit)
	{
		private readonly Lock _gate = new();
		private readonly int _limit = Math.Max(0, limit);
		private readonly StringBuilder _head = new();
		private readonly StringBuilder _tail = new();
		private long _length;

		public async Task ReadAsync(StreamReader reader, CancellationToken token)
		{
			char[] buffer = new char[4096];
			int read;
			// Linux 管道的底层读取可能已阻塞，取消等待不能依赖底层读取先返回。
			while ((read = await reader.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false)) > 0)
			{
				lock (_gate)
				{
					_length += read;
					_head.Append(buffer.AsSpan(0, Math.Min(read, _limit - _head.Length)));
					_tail.Append(buffer.AsSpan(Math.Max(0, read - _limit), Math.Min(read, _limit)));
					if (_tail.Length > _limit) _tail.Remove(0, _tail.Length - _limit);
				}
			}
		}

		public SandboxResult Result(int exitCode, bool timedOut)
		{
			(string text, bool truncated) = SandboxOutput.Fit(_head.ToString(), _tail.ToString(), _length, _limit);
			return new SandboxResult { ExitCode = exitCode, Output = text, TimedOut = timedOut, Truncated = truncated };
		}
	}
}
