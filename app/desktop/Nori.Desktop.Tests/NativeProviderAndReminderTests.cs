using System.Text.Json;
using Nori.Desktop.Settings;
using Avalonia.Controls;

namespace Nori.Desktop.Tests;

/// <summary>原生页面与既有服务协议的功能回归。</summary>
public sealed class NativeProviderAndReminderTests
{
	[Fact]
	public void ChatProviderSwitchMovesDefaultEndpointButPreservesProxy()
	{
		Assert.Equal("https://api.anthropic.com/v1", AiSettingsPage.DefaultBaseUrl("anthropic", "https://api.openai.com/v1"));
		Assert.Equal("https://generativelanguage.googleapis.com/v1beta", AiSettingsPage.DefaultBaseUrl("google", ""));
		Assert.Equal("https://proxy.example/v1", AiSettingsPage.DefaultBaseUrl("google", "https://proxy.example/v1"));
	}

	[Fact]
	public void EmbeddingDimensionsCanBeClearedAndRejectFractionalValues()
	{
		Assert.Equal("", AiSettingsPage.NormalizeDimensions(" "));
		Assert.Equal("1024", AiSettingsPage.NormalizeDimensions(" 1024 "));
		Assert.Throws<ArgumentException>(() => AiSettingsPage.NormalizeDimensions("0"));
		Assert.Throws<ArgumentException>(() => AiSettingsPage.NormalizeDimensions("-1"));
		Assert.Throws<ArgumentException>(() => AiSettingsPage.NormalizeDimensions("12.5"));
	}

	[Fact]
	public void VoiceProviderRoundTripRestoresCompatibleDefaults()
	{
		var gemini = VoiceSettingsPage.ProviderDefaults("gemini", "https://api.openai.com/v1", "tts-1", "nova");
		Assert.Equal("https://generativelanguage.googleapis.com/v1beta", gemini.BaseUrl);
		Assert.Equal("gemini-3.1-flash-tts-preview", gemini.Model);
		Assert.Equal("Kore", gemini.Voice);
		var openai = VoiceSettingsPage.ProviderDefaults("openai", gemini.BaseUrl, gemini.Model, gemini.Voice);
		Assert.Equal(("https://api.openai.com/v1", "tts-1", "nova"), openai);
		var custom = VoiceSettingsPage.ProviderDefaults("gemini", "https://proxy.example/v1", "custom-model", "custom-voice");
		Assert.Equal(("https://proxy.example/v1", "custom-model", "custom-voice"), custom);
	}

	[Theory]
	[InlineData("triggerTime")]
	[InlineData("triggerAt")]
	public void ReminderProjectionUsesMillisecondsAndKeepsRecurrenceStatus(string timestampField)
	{
		DateTimeOffset trigger = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
		JsonElement raw = JsonSerializer.SerializeToElement(new[] {new Dictionary<string, object> {["id"] = "one", ["content"] = "测试提醒", [timestampField] = trigger.ToUnixTimeMilliseconds(), ["repeatDaily"] = true, ["status"] = "claimed"}});
		ProactiveSettingsPage.ReminderItemViewModel reminder = Assert.Single(ProactiveSettingsPage.ReadReminders(raw));
		Assert.Equal(trigger, reminder.TriggerAt);
		Assert.True(reminder.RepeatDaily);
		Assert.Equal("claimed", reminder.Status);
		Assert.Equal("In 15 minutes", ProactiveReminderList.RelativeTime(trigger, trigger.AddMinutes(-15), true));
		Assert.Equal("即将提醒", ProactiveReminderList.RelativeTime(trigger, trigger.AddMinutes(1), false));
	}

	[Fact]
	public async Task DailyReminderFailureRollsBackNewReminderAndKeepsOriginalError()
	{
		List<string> commands = [];
		InvalidOperationException failure = new("重复规则保存失败");
		Task<JsonElement> Execute(string command, object? args, CancellationToken cancellationToken)
		{
			commands.Add(command);
			if (command == "reminder_add") return Task.FromResult(JsonSerializer.SerializeToElement(new {id = "created"}));
			JsonElement payload = JsonSerializer.SerializeToElement(args);
			Assert.Equal("created", payload.GetProperty("id").GetString());
			if (command == "reminder_update") throw failure;
			Assert.False(cancellationToken.IsCancellationRequested);
			return Task.FromResult(JsonSerializer.SerializeToElement(true));
		}
		InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
			() => ProactiveSettingsPage.CreateReminderAsync(Execute, "测试提醒", 15, true, CancellationToken.None));
		Assert.Same(failure, actual);
		Assert.Equal(["reminder_add", "reminder_update", "reminder_cancel"], commands);
	}

	[Fact]
	public async Task OneTimeReminderDoesNotUpdateRecurrence()
	{
		List<string> commands = [];
		Task<JsonElement> Execute(string command, object? args, CancellationToken cancellationToken)
		{
			commands.Add(command);
			return Task.FromResult(JsonSerializer.SerializeToElement(new {id = "created"}));
		}
		await ProactiveSettingsPage.CreateReminderAsync(Execute, "测试提醒", 15, false, CancellationToken.None);
		Assert.Equal(["reminder_add"], commands);
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeAiConnectionGroupsKeepIndependentDraftsAndResults() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		using AiSettingsPage page = new(service);
		SettingsFieldViewModel dimensions = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "embeddingDimensions");
		dimensions.Text = "invalid-dimension";
		Assert.True(await page.FlushConnectionSettingsAsync(false));
		Assert.True(dimensions.IsDirty);
		Assert.False(await page.FlushConnectionSettingsAsync(true));
		Assert.NotEmpty(dimensions.ErrorText);

		page.SetConnectionResult(false, "对话连接成功");
		page.SetConnectionResult(true, "向量连接失败");
		page.ApplySnapshot(JsonSerializer.SerializeToElement(new {ai = new {provider = "openai"}}));
		SettingsFieldViewModel chatResult = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "chatResult");
		SettingsFieldViewModel embeddingResult = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "embeddingResult");
		Assert.Equal("对话连接成功", chatResult.Text);
		Assert.Equal("向量连接失败", embeddingResult.Text);
		Assert.True(chatResult.IsVisible);
		Assert.True(embeddingResult.IsVisible);
	});

	[Fact]
	public Task NativeReminderRefreshDoesNotOverwriteNewerSnapshotOrRaceCreation() => WithSettingsUiAsync(async () =>
	{
		Window window = new();
		using SettingsService service = new(_services, window);
		using ProactiveSettingsPage page = new(service);
		TaskCompletionSource<JsonElement> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? refresh = null;
		try
		{
			window.Show();
			SettingsFieldViewModel draft = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "newReminderText");
			draft.Text = "保留提醒草稿";
			Assert.True(await draft.FlushPendingSavesAsync());
			refresh = page.RefreshRemindersAsync(_ => response.Task);
			SettingsFieldViewModel add = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "addReminder");
			SettingsFieldViewModel reload = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "refreshReminders");
			Assert.False(add.Command!.CanExecute(null));
			Assert.False(reload.Command!.CanExecute(null));
			await page.AddReminderAsync();
			Assert.Empty(_runtime.Proactive.ListReminders());

			page.ApplySnapshot(JsonSerializer.SerializeToElement(new
			{
				app = new {safeMode = false},
				proactive = new {reminders = new[] {new {id = "newer", content = "新快照提醒", triggerTime = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds(), repeatDaily = true, status = "pending"}}},
			}));
			response.SetResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
			await refresh;
			Assert.Equal("newer", Assert.Single(page.Reminders).Id);
			Assert.Equal("保留提醒草稿", draft.Text);
			Assert.True(add.Command!.CanExecute(null));
			Assert.True(reload.Command!.CanExecute(null));
		}
		finally
		{
			response.TrySetResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
			if (refresh is not null) await refresh;
			window.Close();
		}
	});
}
