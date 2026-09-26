using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public class ChatServiceTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-chat-test");
	private readonly Nori.Core.Data.NoriDatabase _database;
	private readonly Nori.Core.Configuration.ConfigStore _config;

	public ChatServiceTests()
	{
		_database = Nori.Core.Data.NoriDatabase.Open(_tempDatabase.Path);
		_config = new Nori.Core.Configuration.ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ChatService_StreamAsync流式回调与动作提取()
	{
		using HttpTestHandler handler = new(req =>
		{
			string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"主人好呀！\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"[nori_motion:smile]\"}}]}\n\ndata: [DONE]\n\n";
			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
			};
		});

		using HttpClient client = new(handler);
		ChatService chat = new(client, _database, _config);

		List<string> chunks = [];
		List<string> motions = [];

		string final = await chat.StreamAsync(
			"openai",
			"https://api.openai.com/v1",
			"key",
			"gpt-4o",
			[new ChatMessageInput {Role = "user", Content = "hello"}],
			chunk => chunks.Add(chunk),
			motion => motions.Add(motion));

		Assert.Equal("主人好呀！", final);
		Assert.Equal(["smile"], motions);
		Assert.Equal(2, chat.GetHistory().Count);
	}

	[Fact]
	public async Task ChatService_普通调用不持久化仍返回动作结果()
	{
		using HttpTestHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
		{
			Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"reply[nori_motion:smile]\"}}]}",
				System.Text.Encoding.UTF8, "application/json")
		});
		using HttpClient client = new(handler);
		ChatService chat = new(client, _database, _config);
		List<string> motions = [];

		string reply = await chat.CompleteAsync("openai", "https://api.openai.com/v1", "key", "gpt-4o",
			[new ChatMessageInput("user", "hello")], motions.Add, persist: false);

		Assert.Equal("reply", reply);
		Assert.Equal(["smile"], motions);
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public async Task ChatService_普通和流式调用使用相同输入校验()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);
		ChatMessageInput[] messages = [new("user", "hello")];
		ChatException complete = await Assert.ThrowsAsync<ChatException>(() =>
			chat.CompleteAsync("openai", "", "key", "gpt-4o", messages, _ => { }));
		ChatException stream = await Assert.ThrowsAsync<ChatException>(() =>
			chat.StreamAsync("openai", "", "key", "gpt-4o", messages, _ => { }, _ => { }));
		Assert.Equal(complete.Message, stream.Message);
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public void ChatService_SaveAndClearHistory()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);

		chat.SaveMessage("user", "你好呀");
		chat.SaveMessage("assistant", "主人好！");

		IReadOnlyList<ChatMessage> history = chat.GetHistory();
		Assert.Equal(2, history.Count);
		Assert.Equal("user", history[0].Role);
		Assert.Equal("你好呀", history[0].Content);
		Assert.Equal("assistant", history[1].Role);
		Assert.Equal("主人好！", history[1].Content);

		chat.ClearHistory();
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public void ChatService_GetHistoryPagedReturnsLatestPageInOrder()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);

		for (int i = 1; i <= 5; i++) chat.SaveMessage("user", $"msg{i}");

		// 首页: 取最新的 2 条, 返回时间正序
		IReadOnlyList<ChatMessage> page = chat.GetHistory(2, 0);
		Assert.Equal(2, page.Count);
		Assert.Equal("msg4", page[0].Content);
		Assert.Equal("msg5", page[1].Content);

		// 翻页: 以本页最早一条的 id 为游标继续向前取
		IReadOnlyList<ChatMessage> older = chat.GetHistory(2, page[0].Id);
		Assert.Equal(2, older.Count);
		Assert.Equal("msg2", older[0].Content);
		Assert.Equal("msg3", older[1].Content);

		// 剩余不足一页时返回余量, 取完之后返回空页
		IReadOnlyList<ChatMessage> last = chat.GetHistory(2, older[0].Id);
		Assert.Single(last);
		Assert.Equal("msg1", last[0].Content);
		Assert.Empty(chat.GetHistory(2, last[0].Id));
	}
}
