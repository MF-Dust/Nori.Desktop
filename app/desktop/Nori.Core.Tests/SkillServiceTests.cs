using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Skills;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>技能服务的远程内容边界、工具可用性与兼容语义测试。</summary>
[Collection("HttpClient.DefaultProxy")]
public sealed class SkillServiceTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-skills");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public SkillServiceTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
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
			skills.InstallFromUrlAsync("https://8.8.8.8/skill.json"));

		Assert.Equal("url", installed.Source);
		Assert.False(installed.Enabled);
		Assert.DoesNotContain(skills.GetEnabled(), skill => skill.Id == installed.Id);
		Assert.True(skills.Toggle(installed.Id, true));
		Assert.Contains(skills.GetEnabled(), skill => skill.Id == installed.Id);

		SkillService reloaded = new(_config, http);
		Assert.Contains(reloaded.GetInstalled(), skill => skill.Id == installed.Id && skill.Enabled);
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
		Assert.True(skills.MeasurePromptCharacters(new HashSet<string>()) > SkillLimits.MaxPromptCharacters);
	}

	[Fact]
	public async Task 远程空内容被拒绝且错误不包含响应正文()
	{
		const string body = "\r\n \t";
		using HttpClient http = CreateHttpClient(body);
		SkillService skills = new(_config, http);

		InvalidOperationException error = await WithoutSystemProxy(() =>
			Assert.ThrowsAsync<InvalidOperationException>(
				() => skills.InstallFromUrlAsync("https://8.8.8.8/empty.skill")));

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
				() => skills.InstallFromUrlAsync("https://8.8.8.8/large.skill")));

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
				() => skills.InstallFromUrlAsync("https://8.8.8.8/secret.skill")));

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
				() => skills.InstallFromUrlAsync("https://8.8.8.8/broken.skill")));

		Assert.Equal("远程技能文件格式无效", error.Message);
		Assert.DoesNotContain(marker, error.Message, StringComparison.Ordinal);
	}

	/// <summary>直接覆盖公开解析入口，保证 frontmatter 元数据与正文分开进入技能记录。</summary>
	[Fact]
	public void SKILL_md的frontmatter与正文分别进入元数据和指令()
	{
		string content = """
			---
			name: My Skill
			description: 测试技能说明
			version: 2.0.0
			tags: a, b
			---
			指令正文第一段。
			""";

		SkillRecord skill = SkillService.ParseSkillMarkdown(content, "https://example.com/SKILL.md");

		Assert.Equal("my-skill", skill.Id);
		Assert.Equal("My Skill", skill.Name);
		Assert.Equal("测试技能说明", skill.Description);
		Assert.Equal("2.0.0", skill.Version);
		Assert.Equal(["a", "b"], skill.Tags);
		Assert.Equal("指令正文第一段。", skill.Instructions);
		Assert.False(skill.Enabled);
		Assert.Equal("url", skill.Source);
		Assert.Equal("https://example.com/SKILL.md", skill.Url);
	}

	[Fact]
	public void 内置技能拒绝卸载且市场技能仍可卸载()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		skills.EnsureLoaded();

		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => skills.Uninstall("code-reviewer"));
		Assert.Equal("内置技能不能卸载", error.Message);
		Assert.Contains(skills.GetInstalled(), skill => skill.Id == "code-reviewer");

		skills.InstallFromMarketplace("language-coach");
		Assert.True(skills.Uninstall("language-coach"));
		Assert.DoesNotContain(skills.GetInstalled(), skill => skill.Id == "language-coach");
	}

	[Fact]
	public void 本地导入预览不含指令并可按选择启用()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		string document = """
			---
			name: Local Note
			description: 本地说明
			category: coding
			tools:
			  - getTime
			---
			这是不应出现在预览里的指令正文
			""";

		SkillImportPreview preview = skills.PreviewLocal(document);
		string json = JsonSerializer.Serialize(preview);
		Assert.DoesNotContain("不应出现在预览里的指令正文", json, StringComparison.Ordinal);
		Assert.Equal("Local Note", preview.Name);
		Assert.Equal("coding", preview.Category);
		Assert.Contains("getTime", preview.Tools);

		SkillRecord saved = skills.CommitLocal(preview.Token, enabled: true);
		Assert.Equal("custom", saved.Source);
		Assert.True(saved.Enabled);
		Assert.Contains("不应出现在预览里的指令正文", saved.Instructions, StringComparison.Ordinal);
		Assert.Throws<InvalidOperationException>(() => skills.CommitLocal(preview.Token, enabled: false));
		Assert.Throws<InvalidOperationException>(() => skills.PreviewLocal("api_key: sk-should-not-import\ninstructions"));
	}

	[Fact]
	public void 预设指令写明要调用的工具()
	{
		foreach (SkillRecord skill in SkillPresets.All)
		{
			foreach (string tool in skill.Tools ?? [])
				Assert.Contains(tool, skill.Instructions, StringComparison.Ordinal);
		}
		Assert.Contains(SkillPresets.All, skill => skill.Name == "代码审查与架构顾问");
	}

	[Fact]
	public void 接近上限的正常技能可以解析且保存时仍截断指令()
	{
		const string header = """
			---
			name: Near Limit
			description: 接近上限
			---

			""";
		string content = header + new string('x', SkillLimits.MaxRemoteDocumentCharacters - header.Length);
		Assert.Equal(SkillLimits.MaxRemoteDocumentCharacters, content.Length);

		SkillRecord parsed = SkillService.ParseSkillMarkdown(content, null);
		Assert.Equal("near-limit", parsed.Id);
		Assert.Equal("Near Limit", parsed.Name);
		Assert.True(parsed.Instructions.Length > SkillLimits.MaxInstructionsCharacters);

		using HttpClient http = new();
		SkillService skills = new(_config, http);
		SkillImportPreview preview = skills.PreviewLocal(content);
		SkillRecord saved = skills.CommitLocal(preview.Token, enabled: false);
		Assert.Equal("near-limit", saved.Id);
		Assert.Equal(SkillLimits.MaxInstructionsCharacters, saved.Instructions.Length);
		Assert.False(saved.Enabled);
	}

	[Fact]
	public void 重复文本和残缺frontmatter被拒绝且不会装成合法技能()
	{
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		string repeated = string.Concat(Enumerable.Repeat("api_key: ", 4000));
		InvalidOperationException sensitive = Assert.Throws<InvalidOperationException>(() => skills.PreviewLocal(repeated));
		Assert.Equal("技能文件包含敏感信息，已拒绝", sensitive.Message);
		Assert.DoesNotContain(skills.GetInstalled(), skill => skill.Source == "custom" && skill.Name.Contains("api_key", StringComparison.Ordinal));

		string filler = new string('a', SkillLimits.MaxRemoteDocumentCharacters);
		InvalidOperationException format = Assert.Throws<InvalidOperationException>(() => skills.PreviewLocal(filler));
		Assert.Equal("技能文件格式无效", format.Message);

		string broken = "---\nname: broken\n" + new string('-', 4000);
		InvalidOperationException incomplete = Assert.Throws<InvalidOperationException>(() => skills.PreviewLocal(broken));
		Assert.Equal("技能文件格式无效", incomplete.Message);
		Assert.NotEqual(SkillService.ContentParseTimeoutMessage, incomplete.Message);
	}

	[Fact]
	public void 生产正则有有限超时且导入路径保留解析超时()
	{
		foreach (string fieldName in new[] { "SensitiveRemoteContentPattern", "SkillFrontmatterPattern", "SkillIdPattern" })
		{
			Regex pattern = (Regex)typeof(SkillService).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
			Assert.Equal(SkillService.ContentMatchTimeout, pattern.MatchTimeout);
			Assert.NotEqual(Regex.InfiniteMatchTimeout, pattern.MatchTimeout);
			Assert.True(pattern.MatchTimeout > TimeSpan.Zero);
		}

		const string document = """
			---
			name: Timeout Probe
			description: 超时传播
			---
			指令正文
			""";
		using HttpClient http = new();
		SkillService skills = new(_config, http);
		int installed = skills.GetInstalled().Count;
		RegexMatchTimeoutException timeout = new("input", "pattern", SkillService.ContentMatchTimeout);
		lock (SkillService.ContentMatchGate)
		{
			Func<string, bool> sensitive = SkillService.MatchSensitiveContent;
			Func<string, string> skillId = SkillService.MatchSkillId;
			Func<string, Match> frontmatter = SkillService.MatchSkillFrontmatter;
			try
			{
				SkillService.MatchSensitiveContent = _ => throw timeout;
				AssertTimeoutKept(skills, document);
				SkillService.MatchSensitiveContent = sensitive;

				SkillService.MatchSkillId = _ => throw timeout;
				AssertTimeoutKept(skills, document);
				SkillService.MatchSkillId = skillId;

				SkillService.MatchSkillFrontmatter = _ => throw timeout;
				AssertTimeoutKept(skills, document);
			}
			finally
			{
				SkillService.MatchSensitiveContent = sensitive;
				SkillService.MatchSkillId = skillId;
				SkillService.MatchSkillFrontmatter = frontmatter;
			}
		}
		Assert.Equal(installed, skills.GetInstalled().Count);
	}

	private static void AssertTimeoutKept(SkillService skills, string document)
	{
		InvalidOperationException local = Assert.ThrowsAny<InvalidOperationException>(() => skills.PreviewLocal(document));
		Assert.Equal(SkillService.ContentParseTimeoutMessage, local.Message);
		Assert.NotEqual("技能文件格式无效", local.Message);
		InvalidOperationException remote = Assert.ThrowsAny<InvalidOperationException>(() => skills.ParseDownloadedSkill(document, "https://8.8.8.8/slow.md"));
		Assert.Equal(SkillService.ContentParseTimeoutMessage, remote.Message);
		Assert.NotEqual("远程技能文件格式无效", remote.Message);
	}

	// 请求由替身处理；公网 IP 字面量避免内容测试依赖真实 DNS，不会连接该地址。
	private static HttpClient CreateHttpClient(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new(new HttpTestHandler(_ => new HttpResponseMessage(status)
		{
			Content = new StringContent(body, Encoding.UTF8, "text/plain"),
		}));

	private static async Task<T> WithoutSystemProxy<T>(Func<Task<T>> action)
	{
		IWebProxy originalProxy = HttpClient.DefaultProxy;
		HttpClient.DefaultProxy = new WebProxy {BypassList = [".*"]};
		try
		{
			return await action();
		}
		finally
		{
			HttpClient.DefaultProxy = originalProxy;
		}
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}
}
