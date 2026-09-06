using System.Net;
using System.Text;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Skills;

namespace Nori.Core.Tests;

/// <summary>技能服务的远程内容边界、工具可用性与兼容语义测试。</summary>
[Collection("HttpClient.DefaultProxy")]
public sealed class SkillServiceTests : IDisposable
{
	private static readonly SemaphoreSlim RemoteRequestGate = new(1, 1);

	private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nori-skills-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public SkillServiceTests()
	{
		_database = NoriDatabase.Open(_databasePath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("1.0.3");
	}

	[Fact]
	public async Task 远程URL技能默认禁用且仍可显式启用()
	{
		const string body = "{\"id\":\"remote-demo\",\"name\":\"网络技能\",\"instructions\":\"远程指令\",\"enabled\":true}";
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		SkillRecord installed = await WithoutSystemProxy(() =>
			skills.InstallFromUrlAsync("https://example.com/skill.json"));

		Assert.Equal("url", installed.Source);
		Assert.False(installed.Enabled);
		Assert.DoesNotContain(skills.GetEnabled(), skill => skill.Id == installed.Id);
		Assert.True(skills.Toggle(installed.Id, true));
		Assert.Contains(skills.GetEnabled(), skill => skill.Id == installed.Id);
	}

	[Fact]
	public void 市场安装与JSON导入继续保持启用兼容语义()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);

		SkillRecord marketplace = skills.InstallFromMarketplace("gaming-partner");
		Assert.Equal("market", marketplace.Source);
		Assert.True(marketplace.Enabled);

		SkillRecord imported = skills.ImportJson(
			"{\"id\":\"imported-skill\",\"name\":\"导入技能\",\"instructions\":\"导入指令\",\"source\":\"url\",\"enabled\":false}");
		Assert.Equal("custom", imported.Source);
		Assert.True(imported.Enabled);
	}

	[Fact]
	public void 技能提示词只声明可用工具并明确列出缺失工具()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		foreach (SkillRecord installed in skills.GetInstalled().ToList()) skills.Toggle(installed.Id, false);
		skills.SaveCustom(new SkillRecord
		{
			Id = "tool-availability",
			Name = "工具可用性测试",
			Instructions = "验证工具清单",
			Tools = ["present-tool", "missing-tool"],
			Enabled = true,
		});

		string prompt = skills.BuildSkillsPrompt(new HashSet<string>(["present-tool"]));

		Assert.Contains("Available tools: present-tool", prompt, StringComparison.Ordinal);
		Assert.Contains("Unavailable tools (未注册或已禁用): missing-tool", prompt, StringComparison.Ordinal);
		Assert.DoesNotContain("Available tools: present-tool, missing-tool", prompt, StringComparison.Ordinal);

		string unknownAvailabilityPrompt = skills.BuildSkillsPrompt();
		Assert.Contains("Available tools: (none)", unknownAvailabilityPrompt, StringComparison.Ordinal);
		Assert.Contains("Unavailable tools (未确认可用): present-tool, missing-tool", unknownAvailabilityPrompt, StringComparison.Ordinal);
	}

	[Fact]
	public void 技能提示词始终受长度上限约束()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		foreach (SkillRecord installed in skills.GetInstalled().ToList()) skills.Toggle(installed.Id, false);
		for (int index = 0; index < 3; index++)
		{
			skills.SaveCustom(new SkillRecord
			{
				Id = $"long-skill-{index}",
				Name = $"长技能 {index}",
				Instructions = new string('x', SkillLimits.MaxInstructionsCharacters),
				Enabled = true,
			});
		}

		string prompt = skills.BuildSkillsPrompt(new HashSet<string>());

		Assert.True(prompt.Length <= SkillLimits.MaxPromptCharacters);
	}

	[Fact]
	public async Task 远程空内容被拒绝且错误不包含响应正文()
	{
		const string body = "\r\n \t";
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		InvalidOperationException error = await WithoutSystemProxy(() =>
			Assert.ThrowsAsync<InvalidOperationException>(
				() => skills.InstallFromUrlAsync("https://example.com/empty.skill")));

		Assert.Equal("远程技能文件内容为空", error.Message);
		Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 远程超长内容被拒绝且错误分类稳定()
	{
		string body = new('x', SkillLimits.MaxRemoteDocumentCharacters + 1);
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		InvalidOperationException error = await WithoutSystemProxy(() =>
			Assert.ThrowsAsync<InvalidOperationException>(
				() => skills.InstallFromUrlAsync("https://example.com/large.skill")));

		Assert.Equal("远程文件超过大小上限", error.Message);
		Assert.DoesNotContain("xxxxx", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 远程敏感内容被拒绝且不会泄露密钥()
	{
		const string secret = "TOP-SECRET-123";
		string body = "{\"id\":\"remote-secret\",\"name\":\"敏感技能\",\"instructions\":\"远程指令\",\"api_key\":\"" + secret + "\"}";
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		InvalidOperationException error = await WithoutSystemProxy(() =>
			Assert.ThrowsAsync<InvalidOperationException>(
				() => skills.InstallFromUrlAsync("https://example.com/secret.skill")));

		Assert.Equal("远程技能文件包含敏感信息，已拒绝", error.Message);
		Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 远程格式错误使用稳定错误且不包含响应正文()
	{
		const string marker = "RESPONSE-BODY-MARKER";
		string body = "{\"id\":\"broken\",\"instructions\":\"" + marker + "\"";
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		InvalidOperationException error = await WithoutSystemProxy(() =>
			Assert.ThrowsAsync<InvalidOperationException>(
				() => skills.InstallFromUrlAsync("https://example.com/broken.skill")));

		Assert.Equal("远程技能文件格式无效", error.Message);
		Assert.DoesNotContain(marker, error.Message, StringComparison.Ordinal);
	}

	private static HttpClient CreateHttpClient(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new(new FixedResponseHandler(body, status));

	private static async Task<T> WithoutSystemProxy<T>(Func<Task<T>> action)
	{
		await RemoteRequestGate.WaitAsync();
		IWebProxy originalProxy = HttpClient.DefaultProxy;
		HttpClient.DefaultProxy = new WebProxy {BypassList = [".*"]};
		try
		{
			return await action();
		}
		finally
		{
			HttpClient.DefaultProxy = originalProxy;
			RemoteRequestGate.Release();
		}
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_databasePath); } catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	private sealed class FixedResponseHandler(string body, HttpStatusCode status) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			HttpResponseMessage response = new(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "text/plain"),
			};
			return Task.FromResult(response);
		}
	}
}
