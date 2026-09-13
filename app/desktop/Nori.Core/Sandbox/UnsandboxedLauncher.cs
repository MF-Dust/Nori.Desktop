using System.Diagnostics;
using System.Text;

namespace Nori.Core.Sandbox;

/// <summary>
/// 无隔离的执行：进程以当前用户身份运行，只有工作目录、环境变量与超时三项约束。
///
/// 这是 Windows 以外平台的当前实现，也是 AppContainer 不可用时的退路。
/// <see cref="Isolation"/> 如实报告为 <see cref="SandboxIsolation.None"/>，调用方据此
/// 决定确认强度 —— 把它当成受限执行来放宽确认是错的。
/// </summary>
public sealed class UnsandboxedLauncher : ISandboxLauncher
{
	/// <inheritdoc />
	public SandboxIsolation Isolation => SandboxIsolation.None;

	/// <inheritdoc />
	public async Task<SandboxResult> RunAsync(
		string commandLine, SandboxPolicy policy, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(policy);
		ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

		(string fileName, string arguments) = CommandLine.Split(commandLine);
		ProcessStartInfo start = new(fileName, arguments)
		{
			WorkingDirectory = policy.WorkspaceRoot,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};

		foreach ((string key, string value) in policy.EnvironmentOverrides)
		{
			start.Environment[key] = value;
		}

		using Process process = Process.Start(start)
			?? throw new InvalidOperationException($"无法启动进程: {fileName}");

		StringBuilder output = new();
		Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		bool timedOut = false;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(policy.Timeout);
		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// 超时与外部取消都要结束整棵进程树：构建工具会派生编译器与节点进程，
			// 只结束父进程会留下持有工作目录文件句柄的孤儿。
			timedOut = !cancellationToken.IsCancellationRequested;
			Terminate(process);
			if (!timedOut) throw;
		}

		output.Append(await stdout.ConfigureAwait(false));
		output.Append(await stderr.ConfigureAwait(false));
		(string text, bool truncated) = SandboxOutput.Fit(output.ToString(), policy.MaxOutputCharacters);

		return new SandboxResult
		{
			ExitCode = timedOut ? -1 : process.ExitCode,
			Output = text,
			TimedOut = timedOut,
			Truncated = truncated,
		};
	}

	/// <inheritdoc />
	/// <remarks>无隔离执行不产生任何持久授权，无需释放。</remarks>
	public void Release(SandboxPolicy policy)
	{
	}

	private static void Terminate(Process process)
	{
		try
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// 进程已退出或平台不支持整树结束；两种情况都不影响结果的判定。
		}
	}
}
