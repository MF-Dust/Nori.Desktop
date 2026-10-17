using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Nori.Startup;

namespace Nori.Desktop.Startup;

/// <summary>启动状态单向发送；连接与写入均在后台，不阻塞宿主。</summary>
internal static class StartupSplash
{
	private static readonly Channel<SplashMessage> Messages = Channel.CreateBounded<SplashMessage>(
		new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
	private static int _finished;

	internal static IDisposable Start()
	{
		string? pipe = Environment.GetEnvironmentVariable("NORI_SPLASH_PIPE");
		// 不把启动管道传给工具、插件启动的子进程。
		Environment.SetEnvironmentVariable("NORI_SPLASH_PIPE", null);
		if (!string.IsNullOrEmpty(pipe)) _ = Task.Run(() => SendAsync(pipe));
		return new Session();
	}

	internal static void Report(int progress, string stage) =>
		Messages.Writer.TryWrite(new SplashMessage(progress, stage));

	internal static void Configure(bool enabled, string theme, string background) =>
		Messages.Writer.TryWrite(new SplashMessage(Enabled: enabled, Theme: theme, Background: background, Version: Nori.Core.ProductVersion.Current));

	internal static void Complete()
	{
		if (Interlocked.Exchange(ref _finished, 1) != 0) return;
		Messages.Writer.TryWrite(new SplashMessage(100, "我来啦", Done: true));
		Messages.Writer.TryComplete();
	}

	private static async Task SendAsync(string pipeName)
	{
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
			using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
			await pipe.ConnectAsync(5000, timeout.Token).ConfigureAwait(false);
			await using StreamWriter writer = new(pipe) { AutoFlush = true };
			await foreach (SplashMessage message in Messages.Reader.ReadAllAsync(timeout.Token).ConfigureAwait(false))
				await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
		{
			// 启动画面是可选展示；其退出、超时或不可用均不改变宿主启动结果。
		}
	}

	private sealed class Session : IDisposable
	{
		public void Dispose() => Complete();
	}
}
