using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Nori.Core.Agent;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>Quick Chat 的独立来源身份、命令边界与会话生命周期。</summary>
public partial class BridgeCommandsTests
{
	private sealed class QuickChatTestSource : INativeChatSource, IDisposable
	{
		private readonly CancellationTokenSource _lifetime = new();
		private int _disposed;

		public QuickChatTestSource(
			string label = WindowLabels.QuickChat,
			NativeChatSurface surface = NativeChatSurface.QuickChat)
		{
			Label = label;
			Surface = surface;
			LifetimeToken = _lifetime.Token;
		}

		public string Label { get; }
		public NativeChatSurface Surface { get; }
		public bool IsVisible { get; set; } = true;
		public Window? Self => null;
		public CancellationToken LifetimeToken { get; }
		public ConcurrentQueue<JsonElement> Events { get; } = new();
		public Action<JsonElement>? Received { get; set; }

		public void PostEvent(string name, object? payload)
		{
			JsonElement value = JsonSerializer.SerializeToElement(payload, BridgeJson.Options);
			Events.Enqueue(value);
			Received?.Invoke(value);
		}

		public void PostResult(long id, object? value, string? error) { }

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
			_lifetime.Cancel();
			_lifetime.Dispose();
		}
	}

	[Fact]
	public async Task QuickChatWhitelistIsEnforcedAtBridgeAndRouterEntries()
	{
		using QuickChatTestSource source = new();
		BridgeCommands commands = CreateCommands();
		BridgeCommandRouter router = new(_services);

		// 历史读取是两个入口都必须保留的最小合法命令。
		Assert.NotNull(await commands.InvokeAsync(source, "chat_history_page", Args(new {limit = 2})));
		Assert.NotNull(await router.InvokeAsync(source, "chat_history_page", Args(new {limit = 2})));

		foreach (string command in new[] {"chat_clear", "clipboard_write_text", "open_url", "stt_start", "stt_stop", "tts_stop"})
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, command, Args(new { })));
			await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(source, command, Args(new { })));
		}
	}

	[Fact]
	public async Task QuickChatRejectsSpoofedLabelAndFakeFullSurface()
	{
		BridgeCommands commands = CreateCommands();
		BridgeCommandRouter router = new(_services);
		IBridgeSource[] spoofed =
		[
			new QuickChatTestSource(WindowLabels.Chat),
			new QuickChatTestSource(WindowLabels.QuickChat, NativeChatSurface.Full),
			new FakeBridgeSource(WindowLabels.QuickChat),
		];

		foreach (IBridgeSource source in spoofed)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "chat_history_page", Args(new { })));
			await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(source, "chat_history_page", Args(new { })));
		}
	}

	[Fact]
	public async Task QuickChatDisposedSourceIsRejectedAtBothEntries()
	{
		QuickChatTestSource source = new();
		source.Dispose();
		BridgeCommands commands = CreateCommands();
		BridgeCommandRouter router = new(_services);

		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "chat_history_page", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(source, "chat_history_page", Args(new { })));
	}

	[Fact]
	public async Task QuickChatApprovalIsScopedToOriginalSource()
	{
		using QuickChatTestSource original = new();
		using QuickChatTestSource other = new();
		BridgeCommands commands = CreateCommands();
		Task<bool> decision = _runtime.RequestApprovalAsync(original, "quick-session", new ToolApprovalRequest
		{
			RequestId = "quick-approval",
			ToolName = "writeFile",
			PermissionLevel = "confirm",
		}, CancellationToken.None);

		Assert.False(Assert.IsType<bool>(await commands.InvokeAsync(other, "approval_respond", Args(new {requestId = "quick-approval", approved = true}))));
		Assert.True(Assert.IsType<bool>(await commands.InvokeAsync(original, "approval_respond", Args(new {requestId = "quick-approval", approved = true}))));
		Assert.True(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public Task QuickChatCancelIsScopedToOriginalSourceAndReleasesOnDispose() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using QuickChatTestSource original = new();
		using QuickChatTestSource other = new();
		string sessionId = runtime.StartChat(original, "quick cancel");
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

		Assert.False(runtime.CancelChat(other, sessionId));
		Assert.True(runtime.CancelChat(original, sessionId));
		await WaitUntilAsync(() => !runtime.IsSessionActive(sessionId));
		using (AgentSessionLease lease = runtime.Engine.ReserveSession("quick-after-cancel")) { }

		string disposedSession = runtime.StartChat(original, "quick dispose");
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		original.Dispose();
		await WaitUntilAsync(() => !runtime.IsSessionActive(disposedSession));
		using AgentSessionLease released = runtime.Engine.ReserveSession("quick-after-dispose");
	});

	[Fact]
	public Task QuickChatMessagesAppearInFullHistory() => WithSettingsUiAsync(async () =>
	{
		NativeChatHttpHandler handler = new()
		{
			StreamBody = "event: done\ndata: {\"text\":\"quick reply\"}\n\n",
		};
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using QuickChatTestSource source = new();
		TaskCompletionSource<JsonElement> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.Received = payload =>
		{
			if (payload.TryGetProperty("type", out JsonElement type) && type.GetString() == "complete")
				completed.TrySetResult(payload);
		};

		runtime.StartChat(source, "quick message");
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		handler.ReleaseStream.TrySetResult();
		await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

		JsonElement page = JsonSerializer.SerializeToElement(
			await CreateCommands().InvokeAsync(source, "chat_history_page", Args(new {limit = 20})),
			BridgeJson.Options);
		string history = page.GetRawText();
		Assert.Contains("quick message", history, StringComparison.Ordinal);
		Assert.Contains("quick reply", history, StringComparison.Ordinal);
	});
}
