using System.ComponentModel;
using System.Diagnostics;

namespace Nori.PluginRuntime;

/// <summary>每个插件上下文独立的应用互操作能力；撤销只取消操作，不终止任何进程。</summary>
internal sealed class PluginApplicationCapability(CancellationToken stoppingToken) : IPluginApplicationCapability, IDisposable
{
	private readonly object _gate = new();
	private readonly CancellationTokenSource _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
	private bool _disposed;

	public async Task<IReadOnlyList<PluginApplicationInfo>> GetRunningAsync(CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource operation = CreateOperationSource(cancellationToken);
		return await Task.Run<IReadOnlyList<PluginApplicationInfo>>(() =>
		{
			operation.Token.ThrowIfCancellationRequested();
			Process[] processes;
			try { processes = Process.GetProcesses(); }
			catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
			{
				throw new PluginException(PluginErrorCodes.InvocationFailed, "无法查询运行中的应用");
			}
			try
			{
				List<PluginApplicationInfo> applications = [];
				foreach (Process process in processes)
				{
					operation.Token.ThrowIfCancellationRequested();
					try { applications.Add(new PluginApplicationInfo(process.Id, process.ProcessName)); }
					catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
					{
						// 进程可能在枚举后退出，或不允许读取名称；不影响其余快照。
					}
				}
				return applications.ToArray();
			}
			finally
			{
				foreach (Process process in processes) process.Dispose();
			}
		}, operation.Token).ConfigureAwait(false);
	}

	public async Task<int> LaunchAsync(PluginApplicationLaunchRequest request, CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource operation = CreateOperationSource(cancellationToken);
		return await Task.Run(() =>
		{
			operation.Token.ThrowIfCancellationRequested();
			if (request is null || request.Arguments is null)
				throw new PluginException(PluginErrorCodes.InvocationFailed, "应用启动请求无效");
			string fileName = ValidateAbsolutePath(request.FileName);
			// Windows 即使 UseShellExecute=false 也会经 cmd.exe 执行批处理，ArgumentList 的引号规则挡不住 cmd 元字符。
			if (OperatingSystem.IsWindows() && Path.GetExtension(fileName) is { } extension
				&& (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
				throw new PluginException(PluginErrorCodes.InvocationFailed, "不允许直接启动批处理文件");
			string? workingDirectory = request.WorkingDirectory is null ? null : ValidateAbsolutePath(request.WorkingDirectory);
			string[] arguments = request.Arguments.ToArray();
			if (arguments.Any(argument => argument is null || argument.Any(char.IsControl)))
				throw new PluginException(PluginErrorCodes.InvocationFailed, "应用启动参数不能包含空值或控制字符");
			if (!File.Exists(fileName))
				throw new PluginException(PluginErrorCodes.InvocationFailed, "应用可执行文件不存在或无法访问");
			if (workingDirectory is not null && !Directory.Exists(workingDirectory))
				throw new PluginException(PluginErrorCodes.InvocationFailed, "应用工作目录不存在或无法访问");

			ProcessStartInfo startInfo = new(fileName) { UseShellExecute = false };
			if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
			foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
			operation.Token.ThrowIfCancellationRequested();
			try
			{
				using Process process = Process.Start(startInfo)
					?? throw new PluginException(PluginErrorCodes.InvocationFailed, "无法启动应用");
				return process.Id;
			}
			catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
			{
				// 系统异常可能包含完整路径或参数，不跨插件边界传递原始消息与内部异常。
				throw new PluginException(PluginErrorCodes.InvocationFailed, "无法启动应用");
			}
		}, operation.Token).ConfigureAwait(false);
	}

	public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource operation = CreateOperationSource(cancellationToken);
		await Task.Run(async () =>
		{
			operation.Token.ThrowIfCancellationRequested();
			if (processId <= 0) throw new PluginException(PluginErrorCodes.InvocationFailed, "应用进程 ID 必须为正数");
			try
			{
				Process process;
				try { process = Process.GetProcessById(processId); }
				catch (ArgumentException)
				{
					operation.Token.ThrowIfCancellationRequested();
					return;
				}
				using (process) await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
			{
				throw new PluginException(PluginErrorCodes.InvocationFailed, "无法等待应用进程退出");
			}
		}, operation.Token).ConfigureAwait(false);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
		}
		_lifetimeSource.Cancel();
		_lifetimeSource.Dispose();
	}

	private CancellationTokenSource CreateOperationSource(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (_disposed || _lifetimeSource.IsCancellationRequested)
				throw new PluginException(PluginErrorCodes.CapabilityUnavailable, "插件应用互操作能力已停止");
			return CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token, cancellationToken);
		}
	}

	private static string ValidateAbsolutePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)
			|| path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(path))
			throw new PluginException(PluginErrorCodes.InvocationFailed, "应用路径必须为有效的绝对路径");
		try { return Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			throw new PluginException(PluginErrorCodes.InvocationFailed, "应用路径必须为有效的绝对路径");
		}
	}
}
