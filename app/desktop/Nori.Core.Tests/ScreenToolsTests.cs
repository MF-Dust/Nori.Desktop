using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Tools;
using Nori.Core.Vision;

namespace Nori.Core.Tests;

/// <summary>
/// 读屏工具。
///
/// 重点是两条：能力不具备时整组不注册（避免模型反复调一件必定失败的工具），以及返回的是
/// **文字而不是图片** —— 图片进了对话历史之后每一轮都要重发，上下文成本会持续累积。
/// </summary>
public sealed class ScreenToolsTests
{
	private sealed class FakeCapture : IScreenCapture
	{
		public int Calls { get; private set; }

		public bool IsAvailable { get; init; } = true;

		public ScreenCaptureResult Result { get; init; } = ScreenCaptureResult.Ok(new CapturedScreen
		{
			Bytes = [1, 2, 3],
			MimeType = "image/jpeg",
			Width = 1280,
			Height = 720,
			Window = "Visual Studio Code",
		});

		public ScreenCaptureResult Capture()
		{
			Calls++;
			return Result;
		}
	}

	private sealed class FakeAnalyzer : IVisionAnalyzer
	{
		public List<string> Questions { get; } = [];

		public CapturedScreen? Received { get; private set; }

		public bool IsConfigured { get; init; } = true;

		public string Answer { get; init; } = "画面里是一个编译错误";

		public Task<string> AnalyzeAsync(string question, CapturedScreen screen, CancellationToken cancellationToken)
		{
			Questions.Add(question);
			Received = screen;
			return Task.FromResult(Answer);
		}
	}

	private static ToolRegistry Registry(IScreenCapture? capture, IVisionAnalyzer? analyzer)
	{
		ToolRegistry registry = new();
		ScreenTools.RegisterAll(registry, capture, analyzer);
		return registry;
	}

	private static async Task<JsonElement> CallAsync(ToolRegistry registry, object args)
	{
		ToolResult result = await registry.ExecuteAsync(
			ScreenTools.ReadScreenName,
			JsonNode.Parse(JsonSerializer.Serialize(args)),
			new ToolContext { Approve = _ => Task.FromResult(true) });
		Assert.True(result.IsSuccess, result.Error);
		return JsonSerializer.SerializeToElement(result.Result);
	}

	// ---- 注册 ----

	[Fact]
	public void 平台不支持截屏时不注册()
	{
		Assert.Null(Registry(new FakeCapture { IsAvailable = false }, new FakeAnalyzer()).Get(ScreenTools.ReadScreenName));
	}

	/// <summary>模型不支持看图时同样不注册：那条路上每次调用都会在请求阶段失败。</summary>
	[Fact]
	public void 模型没配好时不注册()
	{
		Assert.Null(Registry(new FakeCapture(), new FakeAnalyzer { IsConfigured = false }).Get(ScreenTools.ReadScreenName));
	}

	[Fact]
	public void 未授权时不注册()
	{
		// 调用方未授权时传 null，判据在 AppRuntime，此处确认工具侧接得住。
		Assert.Null(Registry(null, null).Get(ScreenTools.ReadScreenName));
	}

	/// <summary>看屏幕比读文件敏感，必须逐次确认。</summary>
	[Fact]
	public void 看屏幕需要逐次确认()
	{
		Assert.Equal("confirm", Registry(new FakeCapture(), new FakeAnalyzer()).Get(ScreenTools.ReadScreenName)!.PermissionLevel);
	}

	/// <summary>
	/// 工具描述必须说清看不了整屏、也看不了后台窗口。
	///
	/// 底层只允许截前台窗口，描述里不写的话模型会去试「看看我的浏览器」，每次都失败。
	/// </summary>
	[Fact]
	public void 描述里说清只能看前台窗口()
	{
		string description = Registry(new FakeCapture(), new FakeAnalyzer()).Get(ScreenTools.ReadScreenName)!.Description;

		Assert.Contains("前台窗口", description, StringComparison.Ordinal);
		Assert.Contains("整个屏幕", description, StringComparison.Ordinal);
	}

	// ---- 执行 ----

	[Fact]
	public async Task 截图交给模型并把回答返回()
	{
		FakeCapture capture = new();
		FakeAnalyzer analyzer = new();

		JsonElement result = await CallAsync(Registry(capture, analyzer), new { question = "这个报错什么意思" });

		Assert.Equal(1, capture.Calls);
		Assert.Equal(["这个报错什么意思"], analyzer.Questions);
		Assert.Equal("画面里是一个编译错误", result.GetProperty("answer").GetString());
		Assert.Equal("Visual Studio Code", result.GetProperty("window").GetString());
	}

	/// <summary>
	/// 返回值里不能出现图片字节。
	///
	/// 工具结果会进对话历史，图片一旦进去之后每一轮都要重发，上下文成本持续累积，而后续轮次
	/// 几乎都不需要再看那张图。
	/// </summary>
	[Fact]
	public async Task 返回的是文字而不是图片()
	{
		JsonElement result = await CallAsync(Registry(new FakeCapture(), new FakeAnalyzer()), new { });

		foreach (JsonProperty property in result.EnumerateObject())
		{
			Assert.NotEqual(JsonValueKind.Array, property.Value.ValueKind);
		}

		Assert.False(result.TryGetProperty("bytes", out _));
		Assert.False(result.TryGetProperty("image", out _));
	}

	[Fact]
	public async Task 省略问题时给出整体描述()
	{
		FakeAnalyzer analyzer = new();

		await CallAsync(Registry(new FakeCapture(), analyzer), new { });

		Assert.Single(analyzer.Questions);
		Assert.Contains("描述", analyzer.Questions[0], StringComparison.Ordinal);
	}

	/// <summary>截不到时把原因原样交回模型，让它据此回话，而不是中断整轮。</summary>
	[Fact]
	public async Task 截不到时报出原因且不调模型()
	{
		FakeAnalyzer analyzer = new();
		ToolRegistry registry = Registry(
			new FakeCapture { Result = ScreenCaptureResult.Fail("目标窗口受到 UIPI 完整性级别限制") }, analyzer);

		ToolResult result = await registry.ExecuteAsync(
			ScreenTools.ReadScreenName,
			JsonNode.Parse("{}"),
			new ToolContext { Approve = _ => Task.FromResult(true) });

		Assert.False(result.IsSuccess);
		Assert.Contains("UIPI", result.Error!, StringComparison.Ordinal);
		Assert.Empty(analyzer.Questions);
	}

	[Fact]
	public async Task 传给模型的就是截到的那张图()
	{
		FakeCapture capture = new();
		FakeAnalyzer analyzer = new();

		await CallAsync(Registry(capture, analyzer), new { });

		Assert.Equal(capture.Result.Screen, analyzer.Received);
	}
}
