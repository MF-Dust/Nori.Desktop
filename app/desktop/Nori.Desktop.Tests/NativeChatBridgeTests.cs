using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Nori.Core.Agent;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class NativeChatTestSource : INativeChatSource, IDisposable
	{
		private readonly CancellationTokenSource _lifetime = new();
		private int _disposed;
		public NativeChatTestSource(string label = WindowLabels.Chat)
		{
			Label = label;
			LifetimeToken = _lifetime.Token;
		}
		public string Label { get; }
		private volatile bool _visible = true;
		public Action? VisibilityRead { get; set; }
		public bool IsVisible
		{
			get { bool visible = _visible; VisibilityRead?.Invoke(); return visible; }
			set => _visible = value;
		}
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
	public async Task NativeChatWindowEntryAllowsOnlyVisibleMain()
	{
		BridgeCommands commands = CreateCommands();
		Assert.Null(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_open_chat", Args(new { })));
		Assert.Equal(1, _windows.ChatShowCount);
		IBridgeSource[] rejected =
		[
			new FakeBridgeSource(WindowLabels.Main, false), new FakeBridgeSource(WindowLabels.Init),
			new FakeBridgeSource(WindowLabels.Chat), new NativeChatTestSource(), new NativeChatTestSource(WindowLabels.Main),
		];
		foreach (IBridgeSource source in rejected)
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "window_open_chat", Args(new { })));
		Assert.Equal(1, _windows.ChatShowCount);
	}

	[Fact]
	public async Task NativeChatWhitelistAndTrustedIdentityAreEnforcedAtBothEntries()
	{
		string[] allowed =
		[
			"approval_extend", "approval_respond", "chat_cancel", "chat_clear", "chat_history_page", "chat_start",
			"clipboard_write_text", "open_url", "stt_start", "stt_stop", "tts_stop",
		];
		Assert.Equal(allowed, NativeChatService.Commands.Order(StringComparer.Ordinal));
		using NativeChatTestSource native = new();
		foreach (string command in new[] {"plugin_action", "plugin_widgets", "settings_update_ai", "audio_host_ready", "ui_get_snapshot", "chat_future"})
		{
			Assert.False(NativeChatService.IsCommandAllowed(command));
			await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(native, command, Args(new { })));
			await Assert.ThrowsAsync<InvalidOperationException>(() => new BridgeCommandRouter(_services).InvokeAsync(native, command, Args(new { })));
		}
		foreach (IBridgeSource spoof in new IBridgeSource[] {new FakeBridgeSource(WindowLabels.Chat), new NativeChatTestSource(WindowLabels.Main)})
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(spoof, "chat_history_page", Args(new { })));
			await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(spoof, "chat_start", Args(new {text = "你好"})));
			await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommands().InvokeAsync(spoof, "open_url", Args(new {url = "https://example.test"})));
		}
	}

	[Fact]
	public async Task NativeChatHistoryFiltersLegacyFeedbackBeforeTakingPage()
	{
		_services.Chat.SaveMessage("user", "最早一条");
		_services.Chat.SaveMessage("assistant", "{\"type\":\"message\",\"text\":\"规范化文本\"}");
		_services.Chat.SaveMessage("user", "最近一条");
		for (int index = 0; index < 55; index++) _services.Chat.SaveMessage("user", "【系统工具执行反馈 - tool】: 旧反馈");
		using NativeChatTestSource source = new();
		JsonElement page = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(source, "chat_history_page", Args(new {limit = 2})), BridgeJson.Options);
		Assert.Equal(2, page.GetArrayLength());
		Assert.Equal("规范化文本", page[0].GetProperty("content").GetString());
		Assert.Equal("最近一条", page[1].GetProperty("content").GetString());
		JsonElement older = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(source, "chat_history_page", Args(new {limit = 2, beforeId = page[0].GetProperty("id").GetInt64()})), BridgeJson.Options);
		Assert.Equal("最早一条", Assert.Single(older.EnumerateArray()).GetProperty("content").GetString());
	}

	[Fact]
	public async Task NativeChatHiddenApprovalAllowsDenialButNotApprovalOrExtension()
	{
		using NativeChatTestSource source = new();
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", new ToolApprovalRequest
		{
			RequestId = "hidden-approval", ToolName = "tool", PermissionLevel = "confirm",
		}, CancellationToken.None);
		source.IsVisible = false;
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "approval_respond", Args(new {requestId = "hidden-approval", approved = true})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "approval_extend", Args(new {requestId = "hidden-approval"})));
		Assert.True(Assert.IsType<bool>(await commands.InvokeAsync(source, "approval_respond", Args(new {requestId = "hidden-approval", approved = false}))));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public async Task NativeChatSafeModePreservesLocalHistoryButRejectsNetwork()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using NativeChatTestSource source = new();
		fixture.ConfigureUnreachableLuoLiCore();
		fixture._services.Chat.SaveMessage("user", "可清空的本地记录");
		foreach (string command in new[] {"chat_start", "stt_start", "stt_stop", "open_url"})
			await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateCommands().InvokeAsync(source, command, Args(new {text = "你好", url = "https://example.test"})));
		Assert.NotNull(await fixture.CreateCommands().InvokeAsync(source, "chat_history_page", Args(new { })));
		await fixture.CreateCommands().InvokeAsync(source, "chat_clear", Args(new { }));
		Assert.Empty(fixture._services.Chat.GetHistory());
		JsonElement snapshot = JsonSerializer.SerializeToElement(fixture._runtime.BuildSnapshot(), BridgeJson.Options);
		Assert.False(snapshot.GetProperty("chat").GetProperty("configured").GetBoolean());
		Assert.DoesNotContain("sk-unreachable", snapshot.GetRawText(), StringComparison.Ordinal);
	}

	[Fact]
	public Task NativeChatServiceSnapshotUsesActualBackendAndDisposalStopsNotifications() => WithSettingsUiAsync(async () =>
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(true));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("https://chat.invalid"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("native-only-secret"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text(""));
		using NativeChatService service = new(_services, new Window());
		int changes = 0;
		service.StateChanged += () => Interlocked.Increment(ref changes);
		JsonElement snapshot = await service.GetSnapshotAsync();
		Assert.True(snapshot.GetProperty("chat").GetProperty("configured").GetBoolean());
		Assert.Equal("luolicore", snapshot.GetProperty("chat").GetProperty("backend").GetString());
		Assert.False(snapshot.GetProperty("ai").GetProperty("configured").GetBoolean());
		Assert.DoesNotContain("native-only-secret", snapshot.GetRawText(), StringComparison.Ordinal);
		Assert.Equal(JsonValueKind.Array, (await service.ExecuteAsync("chat_history_page")).ValueKind);
		_runtime.InvalidateSnapshot("chat");
		Assert.True(changes > 0);
		service.Dispose();
		int before = changes;
		_runtime.InvalidateSnapshot("chat");
		Assert.Equal(before, changes);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetSnapshotAsync());
	});
}
