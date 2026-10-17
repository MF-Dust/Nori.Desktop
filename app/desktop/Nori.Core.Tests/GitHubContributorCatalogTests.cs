using System.Net;
using System.Net.Http.Headers;
using Nori.Core.Community;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class GitHubContributorCatalogTests : IDisposable
{
	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

	private const string Source = """
		[
			{"login":"alice","type":"User","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"},
			{"login":"dependabot[bot]","type":"Bot","avatar_url":"https://avatars.githubusercontent.com/in/29110?v=4"},
			{"login":"mallory","type":"User","avatar_url":"https://evil.example/u/3"},
			{"login":"bob","type":"User","avatar_url":"https://avatars.githubusercontent.com/u/2?v=4"}
		]
		""";

	private readonly string _directory = Directory.CreateTempSubdirectory("nori-contributors-").FullName;

	private string CachePath => Path.Combine(_directory, "github-contributors.json");

	public void Dispose()
	{
		if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
	}

	[Fact]
	public async Task 下载只保留真人与官方头像并写缓存_安全模式只读缓存()
	{
		List<Uri> requests = [];
		using HttpClient http = new(new HttpTestHandler(request =>
		{
			lock (requests) requests.Add(request.RequestUri!);
			Assert.Contains("Nori.Desktop", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
			return Respond(request);
		}));

		await using (GitHubContributorCatalog online = new(http, CachePath, () => true))
		{
			IReadOnlyList<GitHubContributor> contributors = await online.GetAsync();
			Assert.Equal(["alice", "bob"], contributors.Select(contributor => contributor.Login));
			Assert.All(contributors, contributor => Assert.Equal(Png, contributor.Avatar));
		}
		Assert.True(File.Exists(CachePath));
		Assert.Equal(3, requests.Count);
		Assert.Contains(requests, uri => uri.AbsoluteUri == "https://avatars.githubusercontent.com/u/1?v=4&s=96");
		Assert.DoesNotContain(requests, uri => uri.Host == "evil.example");

		await using GitHubContributorCatalog offline = new(http, CachePath, () => false);
		Assert.Equal(["alice", "bob"], (await offline.GetAsync()).Select(contributor => contributor.Login));
		Assert.Equal(3, requests.Count);
	}

	[Fact]
	public async Task 安全模式且无缓存时返回空且不联网()
	{
		int requests = 0;
		using HttpClient http = new(new HttpTestHandler(request =>
		{
			Interlocked.Increment(ref requests);
			return Respond(request);
		}));
		await using GitHubContributorCatalog catalog = new(http, CachePath, () => false);
		Assert.Empty(await catalog.GetAsync());
		Assert.Equal(0, requests);
	}

	[Fact]
	public async Task 缓存过期时先返回旧缓存再后台刷新()
	{
		using MutableTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
		File.WriteAllText(CachePath, GitHubContributorCatalog.ToCacheJson(new GitHubContributorCatalog.ContributorSnapshot(
			time.GetUtcNow(), [new GitHubContributor("old", Png)])));
		TaskCompletionSource listRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int requests = 0;
		using HttpClient http = new(new HttpTestHandler(request =>
		{
			Interlocked.Increment(ref requests);
			if (request.RequestUri!.Host == "api.github.com") listRequested.TrySetResult();
			return Respond(request);
		}));
		await using GitHubContributorCatalog catalog = new(http, CachePath, () => true, time: time);

		Assert.Equal(["old"], (await catalog.GetAsync()).Select(contributor => contributor.Login));
		Assert.Equal(0, requests);

		time.Advance(TimeSpan.FromHours(25));
		Assert.Equal(["old"], (await catalog.GetAsync()).Select(contributor => contributor.Login));
		await listRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await WaitUntilAsync(async () => (await catalog.GetAsync()).Count == 2);
	}

	[Fact]
	public async Task 无缓存时额度用尽给出可读错误且退避期内不重试()
	{
		int requests = 0;
		using HttpClient http = new(new HttpTestHandler(_ =>
		{
			Interlocked.Increment(ref requests);
			HttpResponseMessage response = new(HttpStatusCode.Forbidden) {Content = new StringContent("{}")};
			response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
			return response;
		}));
		await using GitHubContributorCatalog catalog = new(http, CachePath, () => true);

		InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetAsync());
		Assert.Equal("GitHub 接口访问次数已达上限，请稍后再试", first.Message);
		InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetAsync());
		Assert.Equal(first.Message, second.Message);
		Assert.Equal(1, requests);
		Assert.False(File.Exists(CachePath));
	}

	[Fact]
	public void 缓存仓库或版本不符时视为没有缓存()
	{
		Assert.Null(GitHubContributorCatalog.ParseCache("""{"version":1,"repository":"someone/else","fetchedAt":"2026-01-01T00:00:00Z","contributors":[]}"""));
		Assert.Null(GitHubContributorCatalog.ParseCache("""{"version":2,"repository":"MF-Dust/Nori.Desktop","fetchedAt":"2026-01-01T00:00:00Z","contributors":[]}"""));
	}

	[Fact]
	public void 头像只接受常见图片文件头()
	{
		Assert.True(GitHubContributorCatalog.IsSupportedImage(Png));
		Assert.True(GitHubContributorCatalog.IsSupportedImage([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
		Assert.True(GitHubContributorCatalog.IsSupportedImage("RIFF\0\0\0\0WEBPVP8 "u8));
		Assert.False(GitHubContributorCatalog.IsSupportedImage("<html>not an image</html>"u8));
		Assert.False(GitHubContributorCatalog.IsSupportedImage([0x89, 0x50, 0x4E, 0x47]));
	}

	private static HttpResponseMessage Respond(HttpRequestMessage request)
	{
		if (request.RequestUri == GitHubContributorCatalog.SourceUri)
			return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(Source)};
		ByteArrayContent image = new(Png);
		image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
		return new HttpResponseMessage(HttpStatusCode.OK) {Content = image};
	}

	private static async Task WaitUntilAsync(Func<Task<bool>> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);
		while (!await condition())
		{
			if (DateTime.UtcNow > deadline) throw new TimeoutException("等待后台刷新超时");
			await Task.Delay(20);
		}
	}
}
