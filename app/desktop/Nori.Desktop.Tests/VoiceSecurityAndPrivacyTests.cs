using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Tools;
using Nori.Core.Voice.Metrics;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Bridge;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task VoiceMetricsTracker_AppRuntime只为语音轮次采样首个助手增量() => WithRealtimeChatSourceAsync(async (source, _) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set("tts_auto_play", new ConfigValue.Boolean(false));
		Assert.Same(_services.VoiceMetrics, runtime.VoiceMetrics);
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "私密语音转写", CancellationToken.None));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		handler.ReleaseStream.TrySetResult();
		await started.Completion.WaitAsync(TimeSpan.FromSeconds(3));
		var latency = _services.VoiceMetrics.CaptureSnapshot().AgentFirstTokenLatencyMs;
		Assert.Equal(1, latency.Count);
		Assert.True(latency.P50 > 0);
	});

	[Theory]
	[InlineData("realtime_voice_start")]
	[InlineData("stt_start")]
	[InlineData("stt_stop")]
	[InlineData("tts_test")]
	[InlineData("indextts_clone_voice")]
	public async Task VoiceSecurityAndPrivacy_SafeModeRejectsNetworkCommandsAtDispatch(string command)
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), command, Args(new { })));
		Assert.Contains("安全模式", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("trusted")]
	[InlineData("bypass")]
	[InlineData("session")]
	public Task VoiceSecurityAndPrivacy_RealtimeVoiceToolApprovalAlwaysRequiresFreshConsent(string gear) => WithRealtimeChatSourceAsync(async (source, events) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text(gear));
		if (gear == "bypass")
			_config.Set(ToolPermissionPolicy.KeyBypassUntil,
				new ConfigValue.Text(ToolPermissionPolicy.FormatDeadline(DateTimeOffset.UtcNow.AddMinutes(10))));
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "语音请求", CancellationToken.None));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		SetVoiceTestWindowActive(source.Self!, true);
		for (int index = 0; index < 2; index++)
		{
			ToolApprovalRequest approval = new()
			{
				RequestId = $"voice-approval-{gear}-{index}",
				ToolName = "writeFile",
				PermissionLevel = "confirm",
			};
			Task<bool> pending = runtime.RequestApprovalAsync(source, started.SessionId, approval, CancellationToken.None);
			Assert.False(pending.IsCompleted);
			Assert.Contains(events, item =>
				item.GetProperty("type").GetString() == "approval-request"
				&& item.GetProperty("requestId").GetString() == approval.RequestId);
			Assert.True(runtime.RespondApproval(source, approval.RequestId, true));
			Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.False(runtime.Permissions.Remembered(started.SessionId, "writeFile"));
		}
		handler.ReleaseStream.TrySetResult();
		await started.Completion.WaitAsync(TimeSpan.FromSeconds(3));
	});

	[Fact]
	public Task VoiceSecurityAndPrivacy_TextBridgeCannotSetVoiceOrigin() => WithRealtimeChatSourceAsync(async (source, _) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		_config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text("trusted"));
		object? response = await CreateCommands().InvokeAsync(source, "chat_start", Args(new
		{
			text = "文本轮次", origin = "voice", inputOrigin = "voice", isVoice = true, generation = 90,
		}));
		string sessionId = Assert.IsType<string>(response);
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Task<bool> decision = runtime.RequestApprovalAsync(source, sessionId, new ToolApprovalRequest
		{
			RequestId = "spoofed-voice", ToolName = "writeFile", PermissionLevel = "confirm",
		}, CancellationToken.None);
		Assert.True(decision.IsCompletedSuccessfully);
		Assert.True(await decision);
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task VoiceSecurityAndPrivacy_HiddenOrInactiveWindowCannotApproveOrExtend(bool hide) => WithRealtimeChatSourceAsync(async (source, _) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "语音权限审计", CancellationToken.None));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Window window = source.Self!;
		SetVoiceTestWindowActive(window, true);
		Assert.True(source.IsVisible);
		Assert.True(source.IsActive);
		const string requestId = "inactive-approval";
		Task<bool> pending = runtime.RequestApprovalAsync(source, started.SessionId, new ToolApprovalRequest
		{
			RequestId = requestId, ToolName = "writeFile", PermissionLevel = "confirm",
		}, CancellationToken.None);
		Assert.False(pending.IsCompleted);
		if (hide) window.Hide();
		else SetVoiceTestWindowActive(window, false);
		Assert.Equal(!hide, source.IsVisible);
		if (!hide) Assert.False(source.IsActive);
		Assert.Throws<InvalidOperationException>(() => runtime.RespondApproval(source, requestId, true));
		Assert.Throws<InvalidOperationException>(() => runtime.ExtendApproval(source, requestId));
		Assert.True(runtime.RespondApproval(source, requestId, false));
		Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
	});

	[Fact]
	public Task VoiceSecurityAndPrivacy_CancelledTurnCannotReuseApproval() => WithRealtimeChatSourceAsync(async (source, _) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		using CancellationTokenSource turn = new();
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "语音权限审计", turn.Token));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		const string requestId = "cancelled-voice-approval";
		Task<bool> pending = runtime.RequestApprovalAsync(source, started.SessionId, new ToolApprovalRequest
		{
			RequestId = requestId, ToolName = "writeFile", PermissionLevel = "confirm",
		}, CancellationToken.None);
		Assert.False(pending.IsCompleted);
		turn.Cancel();
		Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.False(runtime.RespondApproval(source, requestId, true));
		Assert.Throws<InvalidOperationException>(() => runtime.ExtendApproval(source, requestId));
		Assert.False(runtime.Permissions.Remembered(started.SessionId, "writeFile"));
	});

	[Fact]
	public Task VoiceSecurityAndPrivacy_ConsentCannotOverrideFileNetworkOrDesktopBoundaries() => WithRealtimeChatSourceAsync(async (source, events) =>
	{
		NativeChatHttpHandler handler = new();
		using HttpClient http = new(handler);
		await using AppRuntime runtime = CreateNativeChatRuntime(http);
		string workspace = Directory.CreateDirectory(Path.Combine(_tempDir, "workspace")).FullName;
		_config.Set(ConfigStore.KeyWorkspaceRoot, new ConfigValue.Text(workspace));
		_config.Set(ConfigStore.KeyScreenReadingEnabled, new ConfigValue.Boolean(false));
		_config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text("bypass"));
		_config.Set(ToolPermissionPolicy.KeyBypassUntil, new ConfigValue.Text(ToolPermissionPolicy.FormatDeadline(DateTimeOffset.UtcNow.AddMinutes(2))));
		runtime.RebuildTools();
		var started = runtime.StartRealtimeChat(source, new RealtimeVoiceTurn(90, "语音权限审计", CancellationToken.None));
		await handler.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		SetVoiceTestWindowActive(source.Self!, true);
		ToolContext context = new() { Approve = request => runtime.RequestApprovalAsync(source, started.SessionId, request, CancellationToken.None) };

		async Task<ToolResult> ApproveToolAsync(string name, JsonObject arguments)
		{
			Task<ToolResult> execution = runtime.Tools.ExecuteAsync(name, arguments, context);
			Assert.False(execution.IsCompleted);
			JsonElement request = events.Last(item => item.GetProperty("type").GetString() == "approval-request");
			Assert.Equal(name, request.GetProperty("toolName").GetString());
			Assert.True(runtime.RespondApproval(source, request.GetProperty("requestId").GetString()!, true));
			return await execution.WaitAsync(TimeSpan.FromSeconds(3));
		}

		string outside = Path.Combine(_tempDir, "outside.txt");
		ToolResult file = await ApproveToolAsync("writeFile", new() { ["path"] = outside, ["content"] = "不应写入" });
		Assert.False(file.IsSuccess);
		Assert.Contains("路径超出工作目录范围", file.Error, StringComparison.Ordinal);
		Assert.False(File.Exists(outside));
		ToolResult network = await ApproveToolAsync("fetchWebPage", new() { ["url"] = "http://127.0.0.1/private" });
		Assert.False(network.IsSuccess);
		Assert.Contains("私网或保留地址", network.Error, StringComparison.Ordinal);
		Assert.Null(runtime.Tools.Get(ScreenTools.ReadScreenName));
		Assert.False((await runtime.Tools.ExecuteAsync(ScreenTools.ReadScreenName, null, context)).IsSuccess);
	});

	[Fact]
	public void VoiceSecurityAndPrivacy_DiagnosticZipDoesNotContainVoicePayloadsOrUserPaths()
	{
		const string key = "sk-proj-U4gNzBc8sc1MvLp3WqK6C7";
		const string transcript = "recognizer payload: my private voice sentence";
		const string pathInError = "/home/private-voice-user/recordings/clip.pcm";
		const string endpoint = "https://private-voice.invalid/v1/transcribe";
		byte[] pcm = [0xE2, 0xCC, 0xD5, 0xAD, 0x12, 0xBD, 0x81, 0xFE, 0x43, 0x77, 0x2A, 0x99];
		_config.Set("stt_api_key", new ConfigValue.Text(key));
		_config.Set("stt_base_url", new ConfigValue.Text(endpoint));
		_services.Chat.SaveMessage("user", transcript);
		File.WriteAllBytes(Path.Combine(_tempDir, "clip.pcm"), pcm);
		_services.Logger.Write(LogSource.Backend, "error", $"语音识别失败 api_key={key} {pathInError}",
			exception: new IOException($"{transcript} {endpoint} {Convert.ToBase64String(pcm)}"));

		VoiceMetricsTracker tracker = _runtime.VoiceMetrics;
		Assert.Same(_services.VoiceMetrics, tracker);
		tracker.RecordFirstDeltaLatency(TimeSpan.FromMilliseconds(42));
		tracker.ObserveAudioBufferBytes(pcm.LongLength);
		tracker.ObserveProcessResources(1024, 1.25);
		tracker.RecordDeviceRecoveryFailure();
		string target = Path.Combine(_tempDir, "voice-diagnostics.zip");
		DiagnosticExporter.Result result = DiagnosticExporter.Export(target, _services.Logger, null, _services.Paths,
			safeMode: false, voiceMetrics: tracker.CaptureSnapshot());
		Assert.InRange(result.Bytes, 1, 8L * 1024 * 1024);
		using ZipArchive archive = ZipFile.OpenRead(target);
		string[] files = ["agent-execution.json", "agent-trace.json", "diagnostics.json", "README.txt", "recent-logs.json", "runtime.json", "voice-metrics.json"];
		Assert.Equal(files.Append("manifest.json").Order(StringComparer.Ordinal), archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			using Stream data = entry.Open();
			using MemoryStream unpacked = new();
			data.CopyTo(unpacked);
			byte[] bytes = unpacked.ToArray();
			Assert.True(bytes.AsSpan().IndexOf(pcm) < 0);
			string text = System.Text.Encoding.UTF8.GetString(bytes);
			foreach (string sensitive in new[] { key, transcript, pathInError, endpoint, _services.Paths.DataRoot, "nori.db", "SQLite format 3", Convert.ToBase64String(pcm) })
			{
				Assert.DoesNotContain(sensitive, text, StringComparison.Ordinal);
				if (entry.FullName.EndsWith(".json", StringComparison.Ordinal))
					Assert.DoesNotContain(JsonEncodedText.Encode(sensitive).ToString(), text, StringComparison.Ordinal);
			}
			if (entry.FullName == "voice-metrics.json")
				Assert.Equal(tracker.CaptureSnapshot(), JsonSerializer.Deserialize<VoiceMetricsSnapshot>(bytes));
			if (entry.FullName == "manifest.json")
			{
				using JsonDocument manifest = JsonDocument.Parse(bytes);
				Assert.Equal(files.Order(StringComparer.Ordinal), manifest.RootElement.GetProperty("files").EnumerateArray()
					.Select(file => file.GetString()!).Order(StringComparer.Ordinal));
			}
		}
	}
	// 无头后端不会可靠地产生焦点切换；显式触发 Avalonia 自己的平台回调。
	private static void SetVoiceTestWindowActive(Window window, bool active) =>
		typeof(WindowBase).GetMethod(active ? "HandleActivated" : "HandleDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(window, null);
}
